using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Fonts;
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
    /// <summary>The bundled Arabic font, by its family name inside <see cref="FontsKey"/>.</summary>
    public const string ArabicFontFamily = "fonts:Josour#Noto Naskh Arabic UI";

    private static readonly Uri FontsKey = new("fonts:Josour", UriKind.Absolute);
    private static readonly Uri FontsSource = new("avares://Josour.Mobile/Assets/Fonts", UriKind.Absolute);

    private static ISingleViewApplicationLifetime? _view;

    /// <summary>Set by the Android head before Avalonia initialises.</summary>
    public static MobilePlatform? Platform { get; set; }

    /// <summary>
    /// Registers the bundled Arabic font and makes it the fallback for any character the default font lacks.
    /// <para>
    /// Without it every Arabic letter is an empty box on Android: Avalonia's default family there has no Arabic, and its
    /// per-character fallback does not reach the phone's own Arabic fonts (they are unnamed "und-Arab" fallbacks in
    /// <c>/system/etc/fonts.xml</c>, and vary by manufacturer). Bundling Regular and Bold covers every weight the views use.
    /// </para>
    /// </summary>
    public static AppBuilder UseFonts(AppBuilder builder) => builder
        .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(FontsKey, FontsSource)))
        .With(new FontManagerOptions
        {
            FontFallbacks = [new FontFallback { FontFamily = new FontFamily(ArabicFontFamily) }],
        });

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single && Platform is { } platform)
        {
            // The language is already applied: the Android head does it before the platform exists (MobileLanguage).
            var services = MobileRuntime.Start(platform);
            var main = services.GetRequiredService<MainViewModel>();
            _view = single;
            single.MainView = new MainView { DataContext = main };

            // The strings and the reading direction are read when a view loads, so a new language is a new view.
            main.LanguageChanged -= RebuildView;
            main.LanguageChanged += RebuildView;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RebuildView()
    {
        if (_view is { } view && MobileRuntime.IsStarted)
        {
            view.MainView = new MainView { DataContext = MobileRuntime.Services.GetRequiredService<MainViewModel>() };
        }
    }
}
