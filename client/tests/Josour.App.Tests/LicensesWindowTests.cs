using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Josour.App.Views;

namespace Josour.App.Tests;

public class LicensesWindowTests
{
    [AvaloniaFact]
    public void Legal_texts_are_readable_offline_from_the_built_application()
    {
        using var rendered = new Rendered(new LicensesWindow());
        var text = rendered.Window.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.True(text.IsReadOnly);
        Assert.Contains("Apache License", text.Text);
        Assert.Contains("Limitation of Liability", text.Text);
        Assert.Contains("Fluent System Icons", text.Text);
        Assert.Contains("Copyright (c) 2020 Microsoft Corporation", text.Text);
        Assert.Contains("Permission is hereby granted", text.Text);
    }
}
