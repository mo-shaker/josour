using RouteBridge.App.Services;

namespace RouteBridge.App;

/// <summary>
/// Custom entry point (the csproj sets <c>StartupObject</c>) so single-instance handling runs before WPF spins up.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);

        using var instance = SingleInstanceGuard.TryAcquire();
        if (instance is null)
        {
            // Another RouteBridge is already running for this machine: ask it to show its window and leave quietly.
            SingleInstanceGuard.SignalShowWindow();
            return 0;
        }

        var app = new App(options, instance);
        app.InitializeComponent();
        return app.Run();
    }
}
