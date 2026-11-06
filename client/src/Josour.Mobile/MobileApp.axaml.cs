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
    /// <summary>The bundled typeface, by its family name inside <see cref="FontsKey"/>: Readex Pro, the wordmark's own.</summary>
    public const string UiFontFamily = "fonts:Josour#Readex Pro";

    private static readonly Uri FontsKey = new("fonts:Josour", UriKind.Absolute);
    private static readonly Uri FontsSource = new("avares://Josour.Mobile/Assets/Fonts", UriKind.Absolute);

    private static ISingleViewApplicationLifetime? _view;

    /// <summary>Set by the Android head before Avalonia initialises.</summary>
    public static MobilePlatform? Platform { get; set; }

    /// <summary>
    /// Registers the bundled Readex Pro (Regular, Medium, SemiBold, Bold; Arabic and Latin in one family), which the views
    /// use by name, and makes it the fallback for any character drawn outside them.
    /// <para>
    /// Without it every Arabic letter is an empty box on Android: Avalonia's default family there has no Arabic, and its
    /// per-character fallback does not reach the phone's own Arabic fonts (they are unnamed "und-Arab" fallbacks in
    /// <c>/system/etc/fonts.xml</c>, and vary by manufacturer).
    /// </para>
    /// </summary>
    public static AppBuilder UseFonts(AppBuilder builder) => builder
        .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(FontsKey, FontsSource)))
        .With(new FontManagerOptions
        {
            // Not DefaultFamilyName: on Android that is resolved among the system fonts only, and fails. The views name
            // the family themselves (MainView's FontFamily), and the fallback catches anything drawn outside them.
            FontFallbacks = [new FontFallback { FontFamily = new FontFamily(UiFontFamily) }],
        });

    /// <summary>The phone's font size setting changed while the app runs: build the view again at the new size.</summary>
    public static void ApplyFontScale(double scale)
    {
        if (MobileTheme.SetFontScale(scale))
        {
            RebuildView();
        }
    }

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

    internal static void RebuildView()
    {
        if (_view is { } view && MobileRuntime.IsStarted)
        {
            view.MainView = new MainView { DataContext = MobileRuntime.Services.GetRequiredService<MainViewModel>() };
        }
    }
}
