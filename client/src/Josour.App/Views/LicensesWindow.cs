using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Josour.App.Views;

/// <summary>Offline legal texts, embedded even in a single-file publish.</summary>
public sealed class LicensesWindow : Window
{
    public LicensesWindow()
    {
        Title = Strings.AboutLicenses;
        Width = 720;
        Height = 600;
        MinWidth = 360;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = UiFlow.Direction;

        var layout = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        layout.Children.Add(new TextBlock
        {
            Text = Strings.LicenseSummary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });
        var text = new TextBox
        {
            Text = ReadLegalText("LICENSE") + "\n\n-------------------- NOTICE --------------------\n\n" + ReadLegalText("NOTICE"),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetRow(text, 1);
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
