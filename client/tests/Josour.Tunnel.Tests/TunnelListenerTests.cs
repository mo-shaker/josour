using System.Net;
using System.Net.Sockets;

namespace Josour.Tunnel.Tests;

public class TunnelListenerTests
{
    [Fact]
    public async Task Port_IsAssigned_AndStopClosesTheSocket()
    {
        var listener = new TunnelListener(0, IPAddress.Loopback);
        Assert.InRange(listener.Port, 1, 65535);
        await listener.StartAsync(async (socket, ct) => { socket.Dispose(); await Task.CompletedTask; }, CancellationToken.None);
        Assert.True(listener.IsRunning);

        using (var probe = new TcpClient())
        {
            await probe.ConnectAsync(IPAddress.Loopback, listener.Port);
        }

        await listener.StopAsync();
        Assert.False(listener.IsRunning);

        // The claim is that nothing is accepting on that port any more, so the only outcome that fails
        // this test is a connect that SUCCEEDS. How a closed port announces itself is the operating
        // system's business, not ours: most machines answer RST (SocketException), and some - Windows 11
        // on ARM64 among them - silently drop the SYN, which surfaces as a timeout. Asserting the RST
        // asserted the OS, and failed every run on a machine whose loopback drops instead of refusing.
        using var after = new TcpClient();
        var refused = await Record.ExceptionAsync(
            () => after.ConnectAsync(IPAddress.Loopback, listener.Port).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.NotNull(refused);
        Assert.True(refused is SocketException or TimeoutException, $"unexpected {refused.GetType().Name}: {refused.Message}");
        Assert.False(after.Connected);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task CapsPendingUnauthenticatedConnectionsAtFour()
    {
        await using var listener = new TunnelListener(0, IPAddress.Loopback);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await listener.StartAsync(async (socket, ct) =>
        {
            try
            {
                await Task.WhenAny(release.Task, Task.Delay(System.Threading.Timeout.Infinite, ct));
            }
            finally
            {
                socket.Dispose();
            }
        }, CancellationToken.None);

        var clients = new List<TcpClient>();
        var resetOnConnect = 0;
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, listener.Port);
                    clients.Add(client);
                }
                catch (SocketException)
                {
                    // الإغلاق الفوري (Close(0) = RST) قد يسبق اكتمال connect على هذا النظام: رفض أيضًا.
                    resetOnConnect++;
                    client.Dispose();
                }
            }

            // InboundAttempts يزداد قبل قرار الرفض، فالانتظار عليه وحده يترك سباقًا مع Pending/RejectedOverCapacity.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((listener.InboundAttempts < 6 || listener.RejectedOverCapacity < 2) && DateTime.UtcNow < deadline) await Task.Delay(20);

            Assert.Equal(6, listener.InboundAttempts);
            Assert.Equal(TunnelListener.MaxPendingUnauthenticated, listener.Pending);
            Assert.Equal(2, listener.RejectedOverCapacity);

            var closed = resetOnConnect;
            foreach (var client in clients)
                if (await StreamAssert.IsClosedWithinAsync(client.GetStream(), TimeSpan.FromMilliseconds(500))) closed++;
            Assert.Equal(2, closed);

            release.SetResult();
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (listener.Pending > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(0, listener.Pending);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    [Fact]
    public async Task StartAsync_Twice_Throws()
    {
        await using var listener = new TunnelListener(0, IPAddress.Loopback);
        await listener.StartAsync((s, _) => { s.Dispose(); return Task.CompletedTask; }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.StartAsync((s, _) => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task DefaultBind_IsDualModeOrIpv4Fallback()
    {
        await using var listener = new TunnelListener(0);
        Assert.InRange(listener.Port, 1, 65535);
        await listener.StartAsync((s, _) => { s.Dispose(); return Task.CompletedTask; }, CancellationToken.None);
        using var v4 = new TcpClient(AddressFamily.InterNetwork);
        await v4.ConnectAsync(IPAddress.Loopback, listener.Port);
    }
}
