using Avalonia;
using Avalonia.Headless;
using Josour.App;

[assembly: AvaloniaTestApplication(typeof(Josour.App.Tests.TestAppBuilder))]

namespace Josour.App.Tests;

/// <summary>
/// The application the headless tests run inside.
/// <para>
/// It builds the real <see cref="App"/> — the same class, the same <c>App.axaml</c>, the same theme and resource
/// dictionaries — so a broken icon key or a missing control theme fails here rather than in front of a user. What it
/// does not do is start the Generic Host: <see cref="App.OnFrameworkInitializationCompleted"/> only builds that under
/// a desktop lifetime, and the headless lifetime is not one.
/// </para>
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
