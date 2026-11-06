using Avalonia.Controls;

namespace Josour.App.Theme;

/// <summary>
/// The desktop type scale for the interface language (docs/design/brand-tokens.json, <c>desktop.font</c>): Theme/Metrics.axaml
/// holds each role's size and line height for English (<c>.En</c>) and Arabic (<c>.Ar</c>), and this publishes the pair for the
/// current language as <c>Josour.FontSize.{Role}</c> and <c>Josour.LineHeight.{Role}</c>, which the styles read. Arabic keeps
/// the same sizes with taller lines, and captions at 13 rather than 12.
/// </summary>
public static class DesktopTheme
{
    private static readonly string[] Roles = { "Display", "Title", "Heading", "Body", "Label", "Caption", "Micro" };

    public static void Apply(IResourceDictionary resources, bool arabic)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var suffix = arabic ? ".Ar" : ".En";
        foreach (var role in Roles)
        {
            if (resources.TryGetResource("Josour.FontSize." + role + suffix, null, out var size))
            {
                resources["Josour.FontSize." + role] = size;
            }

            if (resources.TryGetResource("Josour.LineHeight." + role + suffix, null, out var line))
            {
                resources["Josour.LineHeight." + role] = line;
            }
        }
    }
}
