using Josour.Core.Tunnel;

namespace Josour.Tunnel.Transport;

/// <summary>إعداد <see cref="RelayTransport"/>. التوكن يأتي من الخادم الخلفي ويُطلب عند كل اتصال ليمكن تجديده.</summary>
public sealed record RelayTransportOptions
{
    /// <summary>معرّف الجلسة نفسه الذي في session.created (يزاوج به الـ Relay الطرفين).</summary>
    public required Guid SessionId { get; init; }

    public required TunnelRole Role { get; init; }

    /// <summary>توكن جلسة موقَّع من الخادم الخلفي. لا يُسجَّل ولا يظهر في التشخيص.</summary>
    public required Func<CancellationToken, ValueTask<string>> TokenProvider { get; init; }

    /// <summary>عنوان الـ Relay من الإعداد. null = يُستعمل المرشح الممرَّر إلى ConnectAsync كما هو.</summary>
    public CandidateEndpoint? Endpoint { get; init; }

    /// <summary>مهلة تبادل المقدمة والرد بعد نجاح TCP.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>النقل الذي يُفتح به المقبس إلى الـ Relay (Direct افتراضيًا؛ قابل للاستبدال في الاختبارات).</summary>
    public ITunnelTransport Inner { get; init; } = new DirectTransport();
}

/// <summary>
/// نقل عبر Relay: يفتح TCP إلى الـ Relay، يرسل مقدمة <see cref="RelayProtocol"/> (سحر، إصدار، دور، session_id، توكن موقَّع)،
/// وينتظر <see cref="RelayStatus.Paired"/> قبل أن يسلّم الـ stream لأعلى. ما بعد ذلك بايتات معتمة: TLS والمصادقة يجريان
/// بين الجهازين كما في المباشر تمامًا (ADR-0003 البند 5: النقل خلف <see cref="ITunnelTransport"/> من اليوم الأول).
///
/// أثر التفعيل على بقية النظام: صفر. <c>TunnelSessionOptions.Transport = new RelayTransport(...)</c> فقط، ولا يتغير
/// <see cref="SymmetricConnector"/> ولا <see cref="Mux.NerdbankMux"/> ولا سياسة الخروج.
///
/// WEEK 5/6: خدمة الـ Relay نفسها (تحقق التوكن، الاقتران، ضخ البايتات، الحدود) تُبنى فقط إن أغلقت بوابة ADR-0003.
/// الاختبارات اليوم تشغّل Relay وهميًا داخل العملية يزاوج مقبسين.
///
/// نقطة مفتوحة للأسبوع 5/6 (لا تمس عقد اليوم): في المباشر يكون المستمع هو TLS Server، أما فوق الـ Relay فالطرفان
/// متصلان، فيجب تثبيت من يقدّم الشهادة. الأرجح: المضيف دائمًا TLS Server (كما هو مرجع القبول)، والمقدمة تحمل الدور
/// أصلًا فلا يتغير شيء في هذا الملف. لذلك اختبارات اليوم تتحقق من البايتات المعتمة لا من TLS فوق الـ Relay.
/// </summary>
public sealed class RelayTransport : ITunnelTransport
{
    private readonly RelayTransportOptions _options;

    public RelayTransport(RelayTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.TokenProvider);
        ArgumentNullException.ThrowIfNull(options.Inner);
    }

    public string Name => "relay";

    /// <summary>عنوان الـ Relay الفعلي المستعمل لمرشح ما (الإعداد يغلب المرشح).</summary>
    public CandidateEndpoint Target(CandidateEndpoint candidate) => _options.Endpoint ?? candidate;

    public async Task<Stream> ConnectAsync(CandidateEndpoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var stream = await _options.Inner.ConnectAsync(Target(endpoint), timeout, ct).ConfigureAwait(false);
        try
        {
            var token = await _options.TokenProvider(ct).ConfigureAwait(false);
            var preamble = RelayProtocol.BuildPreamble(_options.SessionId, _options.Role, token);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_options.HandshakeTimeout);
                try
                {
                    await stream.WriteAsync(preamble, cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("timed out sending the relay preamble");
                }
            }

            var status = await RelayProtocol.ReadReplyAsync(stream, _options.HandshakeTimeout, ct).ConfigureAwait(false);
            if (status != RelayStatus.Paired) throw new RelayRejectedException(status);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
