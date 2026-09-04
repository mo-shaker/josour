using System.Buffers;

namespace RouteBridge.Tunnel.Mux;

public sealed record PumpResult(long BytesAToB, long BytesBToA, Exception? Error);

/// <summary>
/// ضخ ثنائي الاتجاه بين stream-ين مع نشر الإغلاق النصفي: نهاية القراءة من A → CompleteWriting على B (وبالعكس).
/// ينتهي عندما ينتهي الاتجاهان، أو فورًا عند خطأ في أحدهما (فيُلغى الآخر). لا يتخلص من الـ streams؛ المستدعي يملكها.
/// </summary>
public static class StreamPump
{
    public const int BufferSize = 16 * 1024;

    public static async Task<PumpResult> RunAsync(Stream a, Stream b, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ab = CopyAndHalfCloseAsync(a, b, cts);
        var ba = CopyAndHalfCloseAsync(b, a, cts);
        var (bytesAB, errorAB) = await ab.ConfigureAwait(false);
        var (bytesBA, errorBA) = await ba.ConfigureAwait(false);
        return new PumpResult(bytesAB, bytesBA, errorAB ?? errorBA);
    }

    private static async Task<(long Bytes, Exception? Error)> CopyAndHalfCloseAsync(Stream source, Stream destination, CancellationTokenSource cts)
    {
        long total = 0;
        try
        {
            total = await CopyAsync(source, destination, cts.Token).ConfigureAwait(false);
            if (destination is IHalfClosable half) await half.CompleteWritingAsync(cts.Token).ConfigureAwait(false);
            return (total, null);
        }
        catch (Exception e)
        {
            cts.Cancel();
            return (total, e is OperationCanceledException ? null : e);
        }
    }

    /// <summary>ينسخ حتى نهاية المصدر ويعيد عدد البايتات. الكتابة تُدفع (Flush) بعد كل قطعة.</summary>
    public static async Task<long> CopyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var n = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
                if (n == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
                total += n;
            }
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
