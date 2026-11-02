using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Josour.Infrastructure.Localization;
using Josour.Mobile.Platform;
using Josour.Mobile.ViewModels;
using Josour.Mobile.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Josour.Mobile;

/// <summary>
/// The Avalonia application on the phone. Android supplies <see cref="Platform"/> before the framework starts; the views are
/// built on the process-wide runtime (<see cref="MobileRuntime"/>), so a recreated activity shows the same state.
/// </summary>
public sealed class MobileApp : Application
{
    /// <summary>Set by the Android head before Avalonia initialises.</summary>
    public static MobilePlatform? Platform { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single && Platform is { } platform)
        {
            // Arabic or English as the phone itself is set; Arabic, the default, for any other language.
            ApplyLanguage();
            var services = MobileRuntime.Start(platform);
            single.MainView = new MainView { DataContext = services.GetRequiredService<MainViewModel>() };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyLanguage()
    {
        var system = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var language = !UiLanguages.TryParse(system, out var parsed)
            ? UiLanguages.Default
            : parsed;
        var pack = LocalizedStrings.UseLanguage(language);
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = pack.Culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = pack.Culture;
    }
}
