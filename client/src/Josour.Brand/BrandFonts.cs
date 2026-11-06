using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Josour.Brand;

/// <summary>Readex Pro, the wordmark's own typeface, for Arabic and Latin in both apps.</summary>
public static class BrandFonts
{
    /// <summary>The bundled typeface, by its family name inside the <c>fonts:Josour</c> collection.</summary>
    public const string UiFontFamily = "fonts:Josour#Readex Pro";

    private static readonly Uri FontsKey = new("fonts:Josour", UriKind.Absolute);
    private static readonly Uri FontsSource = new("avares://Josour.Brand/Assets/Fonts", UriKind.Absolute);

    /// <summary>
    /// Registers Readex Pro (Regular, Medium, SemiBold, Bold) and makes it the fallback for any character drawn outside a
    /// view that names it. <paramref name="extraFallbacks"/> come after it, for characters Readex Pro does not have.
    /// <para>
    /// Not <c>DefaultFamilyName</c>: on Android that is resolved among the system fonts only, and fails. The views name the
    /// family themselves (<c>Josour.Font</c>), and the fallback catches anything drawn outside them.
    /// </para>
    /// </summary>
    public static AppBuilder Use(AppBuilder builder, params FontFamily[] extraFallbacks) => builder
        .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(FontsKey, FontsSource)))
        .With(new FontManagerOptions
        {
            FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily(UiFontFamily) } }
                .Concat(extraFallbacks.Select(f => new FontFallback { FontFamily = f }))
                .ToArray(),
        });

    /// <summary>The SIL Open Font License text that ships with the fonts.</summary>
    public static string LicenseText()
    {
        using var stream = typeof(BrandFonts).Assembly.GetManifestResourceStream("Josour.Legal.OFL")
            ?? throw new InvalidOperationException("Missing embedded font licence");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>The system's "reduce motion" setting, as each app read it: the motif stays still when it is set.</summary>
public static class BrandMotion
{
    public static bool ReduceMotion { get; set; }
}
