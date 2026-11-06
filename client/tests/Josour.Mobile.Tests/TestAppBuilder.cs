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
    static TestAppBuilder() => LocalizedStrings.UseLanguage(MobileLanguage.Default);

    /// <summary>
    /// Through <see cref="MobileApp.UseFonts"/>, as the Android head builds it, drawn with Skia: the bundled Readex Pro is the
    /// default family, so text needs a real font manager (and frames can be captured to compare with the mockups).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        MobileApp.UseFonts(AppBuilder.Configure<MobileApp>())
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
