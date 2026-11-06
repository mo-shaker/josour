using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Josour.Brand;

namespace Josour.App.Views;

/// <summary>Offline legal texts, embedded even in a single-file publish: the licence, the notices and the font's OFL.</summary>
public sealed class LicensesWindow : Window
{
    public LicensesWindow()
    {
        Title = Strings.AboutLicenses;
        Width = 720;
        Height = 600;
        MinWidth = 400;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = UiFlow.Direction;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Josour/Assets/josour.ico")));

        var layout = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
        };
        var heading = new TextBlock { Text = Strings.AboutLicenses, Margin = new Thickness(0, 0, 0, 4) };
        heading.Classes.Add("title");
        layout.Children.Add(heading);

        var summary = new TextBlock { Text = Strings.LicenseSummary, Margin = new Thickness(0, 0, 0, 12) };
        summary.Classes.Add("body");
        summary.Classes.Add("secondary");
        Grid.SetRow(summary, 1);
        layout.Children.Add(summary);

        var text = new TextBox
        {
            Text = ReadLegalText("LICENSE")
                + "\n\n-------------------- NOTICE --------------------\n\n" + ReadLegalText("NOTICE")
                + "\n\n-------------------- Readex Pro: SIL Open Font License 1.1 --------------------\n\n" + BrandFonts.LicenseText(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            FontFamily = new FontFamily("Menlo, Consolas, Courier New, monospace"),
            [!TextBox.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Josour.Brush.SurfaceSunken"),
        };
        Grid.SetRow(text, 2);
        layout.Children.Add(text);
        Content = layout;
    }

    private static string ReadLegalText(string name)
    {
        using var stream = typeof(LicensesWindow).Assembly.GetManifestResourceStream("Josour.Legal." + name)
            ?? throw new InvalidOperationException("Missing embedded legal resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
