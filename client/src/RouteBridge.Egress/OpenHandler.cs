using RouteBridge.Tunnel.Mux;

namespace RouteBridge.Egress;

/// <summary>
/// يربط IMuxAcceptor بـ EgressPolicy على المضيف (docs/protocol.md القسم 6 الخطوة 8): الحدود ← السياسة ← OPEN_OK ثم ضخ ثنائي
/// الاتجاه (ينفذه الـ Mux) مع عدّ البايتات وجمع النطاقات المميزة. المعالج لا يرمي أبدًا.
/// </summary>
public sealed class OpenHandler
{
    private readonly EgressPolicy _policy;
    private readonly StreamLimiter _limiter;
    private int _opensOk;
    private int _opensFailed;

    public OpenHandler(EgressPolicy policy, StreamLimiter? limiter = null, ByteCounter? counter = null, DomainCollector? domains = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _limiter = limiter ?? new StreamLimiter();
        Counter = counter ?? new ByteCounter();
        Domains = domains ?? new DomainCollector();
    }

    public ByteCounter Counter { get; }
    public DomainCollector Domains { get; }
    public StreamLimiter Limiter => _limiter;
    public int OpensOk => Volatile.Read(ref _opensOk);
    public int OpensFailed => Volatile.Read(ref _opensFailed);

    /// <summary>يسجل هذا المعالج على الـ acceptor.</summary>
    public void Attach(IMuxAcceptor acceptor)
    {
        ArgumentNullException.ThrowIfNull(acceptor);
        acceptor.OpenRequested = HandleAsync;
    }

    public async Task<MuxOpenDecision> HandleAsync(MuxOpenRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lease = _limiter.TryAcquire();
        if (lease is null)
        {
            Interlocked.Increment(ref _opensFailed);
            return MuxOpenDecision.Fail(OpenFailReason.Limit);
        }

        try
        {
            EgressResult result;
            try
            {
                result = await _policy.OpenAsync(request.Host, request.Port, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lease.Dispose();
                Interlocked.Increment(ref _opensFailed);
                return MuxOpenDecision.Fail(OpenFailReason.ConnectFailed);
            }

            if (!result.IsOk)
            {
                lease.Dispose();
                Interlocked.Increment(ref _opensFailed);
                return MuxOpenDecision.Fail(result.Reason ?? OpenFailReason.ConnectFailed);
            }

            Domains.Add(result.NormalizedHost!);
            Interlocked.Increment(ref _opensOk);
            // الكتابة إلى الموقع = Up، القراءة منه = Down. الحجز يُحرَّر عندما يتخلص الـ Mux من الـ stream بعد انتهاء الضخ.
            var counted = new EgressCountingStream(result.Stream!, Counter, lease);
            return MuxOpenDecision.Ok(counted);
        }
        catch (Exception)
        {
            lease.Dispose();
            Interlocked.Increment(ref _opensFailed);
            return MuxOpenDecision.Fail(OpenFailReason.ConnectFailed);
        }
    }

    /// <summary>CountingStream يغذي ByteCounter المشترك ويحرر حجز الحد عند التخلص.</summary>
    private sealed class EgressCountingStream : Stream, IHalfClosable
    {
        private readonly Stream _inner;
        private readonly ByteCounter _counter;
        private readonly StreamLimiter.Lease _lease;
        private int _disposed;

        public EgressCountingStream(Stream inner, ByteCounter counter, StreamLimiter.Lease lease)
        {
            _inner = inner;
            _counter = counter;
            _lease = lease;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _counter.AddDown(n);
            return n;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _counter.AddUp(buffer.Length);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public ValueTask CompleteWritingAsync(CancellationToken ct)
            => _inner is IHalfClosable h ? h.CompleteWritingAsync(ct) : ValueTask.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _inner.Dispose();
                _lease.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
                _lease.Dispose();
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
