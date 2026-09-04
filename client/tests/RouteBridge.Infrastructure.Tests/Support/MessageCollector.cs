using System.Threading.Channels;
using RouteBridge.Core.Control;

namespace RouteBridge.Infrastructure.Tests.Support;

/// <summary>Buffers every <see cref="IControlChannel.MessageReceived"/> frame so tests can await them in order.</summary>
public sealed class MessageCollector : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IControlChannel _channel;
    private readonly Channel<ControlMessage> _queue = Channel.CreateUnbounded<ControlMessage>();
    private readonly object _gate = new();
    private readonly List<ControlMessage> _all = new();

    public MessageCollector(IControlChannel channel)
    {
        _channel = channel;
        _channel.MessageReceived += OnMessage;
    }

    /// <summary>Every frame seen so far, in delivery order.</summary>
    public IReadOnlyList<ControlMessage> All
    {
        get
        {
            lock (_gate)
            {
                return _all.ToList();
            }
        }
    }

    /// <summary>Consumes frames until one of type <typeparamref name="T"/> (matching <paramref name="where"/>) arrives; other frames are skipped.</summary>
    public async Task<T> NextAsync<T>(Func<T, bool>? where = null, TimeSpan? timeout = null)
        where T : ControlMessage
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        try
        {
            while (true)
            {
                var message = await _queue.Reader.ReadAsync(cts.Token);
                if (message is T typed && (where is null || where(typed)))
                {
                    return typed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No {typeof(T).Name} arrived within {(timeout ?? DefaultTimeout).TotalSeconds:0.#} s. Seen: {string.Join(", ", All.Select(m => m.Type))}");
        }
    }

    /// <summary>Waits <paramref name="quiet"/> and asserts nothing of type <typeparamref name="T"/> arrived in the meantime.</summary>
    public async Task ExpectNoneAsync<T>(TimeSpan quiet)
        where T : ControlMessage
    {
        var before = All.Count;
        await Task.Delay(quiet);
        var arrived = All.Skip(before).OfType<T>().ToList();
        if (arrived.Count > 0)
        {
            throw new Xunit.Sdk.XunitException($"Expected no {typeof(T).Name}, but {arrived.Count} arrived.");
        }
    }

    public int IndexOf(ControlMessage message)
    {
        lock (_gate)
        {
            return _all.IndexOf(message);
        }
    }

    private void OnMessage(ControlMessage message)
    {
        lock (_gate)
        {
            _all.Add(message);
        }

        _queue.Writer.TryWrite(message);
    }

    public void Dispose() => _channel.MessageReceived -= OnMessage;
}
