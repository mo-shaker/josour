using Avalonia;
using Avalonia.Headless;
using Josour.Infrastructure.Localization;
using Josour.Mobile;

[assembly: AvaloniaTestApplication(typeof(Josour.Mobile.Tests.TestAppBuilder))]

namespace Josour.Mobile.Tests;

/// <summary>
/// The phone's own <see cref="MobileApp"/> (its theme and styles) on the headless platform. With no single-view lifetime and no
/// <see cref="MobileApp.Platform"/> it never starts the runtime, so no test signs in anywhere or touches this machine's data.
/// </summary>
public static class TestAppBuilder
{
    static TestAppBuilder() => LocalizedStrings.UseLanguage(UiLanguages.Default);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MobileApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
