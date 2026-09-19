using Avalonia;
using Avalonia.Headless;
using Josour.App;
using Josour.Infrastructure.Localization;

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
    /// <summary>
    /// Fixes the interface language before anything reads it.
    /// <para>
    /// <see cref="UiFlow"/> resolves the reading direction once, on first use, because the real app fixes the
    /// language in <c>App.Start</c> before any window exists. A test process has no such guarantee: a plain
    /// <c>[Fact]</c> that touches <see cref="Strings"/> runs before the Avalonia application is built and freezes
    /// the direction at whatever the default culture happened to be. Doing it here, in the type initializer that
    /// runs before any test, gives every test the same language the app has.
    /// </para>
    /// </summary>
    static TestAppBuilder() => LocalizedStrings.UseLanguage(UiLanguages.Default);

    /// <summary>
    /// Built through <see cref="Program.ConfigureApp"/> — the same method the real entry point uses — so the tests
    /// run on the app's own fonts and options rather than on a second, quietly different configuration. Only the
    /// windowing backend differs, which is the one thing a headless run must change.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        Program.ConfigureApp(AppBuilder.Configure<App>()).UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
