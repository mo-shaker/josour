using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Josour.App.Controls;

namespace Josour.App.Tests;

/// <summary>
/// The three controls written by hand to replace WPF-UI: <see cref="Icon"/>, <see cref="Card"/> and
/// <see cref="InfoBar"/>. They are the pieces with no library behind them, so they are the pieces worth testing.
/// </summary>
public class ControlsTests
{
    /// <summary>Every symbol any view asks for. A name that does not resolve draws nothing at all, silently.</summary>
    public static TheoryData<string> Symbols => new()
    {
        "ArrowSync", "Checkmark", "Desktop", "Dismiss", "ErrorCircle", "Globe",
        "Info", "Key", "Mail", "PlugDisconnected", "Send", "Settings", "Timer", "Warning",
    };

    private static Rendered Render(Control content) => new(content, width: 200, height: 200);

    [AvaloniaTheory]
    [MemberData(nameof(Symbols))]
    public void Every_symbol_the_views_use_resolves_to_a_geometry(string symbol)
    {
        var icon = new Icon { Symbol = symbol };

        using var rendered = Render(icon);

        Assert.NotNull(icon.Data);
        Assert.NotEqual(default, icon.Data!.Bounds);
    }

    [AvaloniaFact]
    public void An_unknown_symbol_draws_nothing_rather_than_throwing()
    {
        // An icon is decoration; a typo in one must not take a window down.
        var icon = new Icon { Symbol = "NoSuchSymbol" };

        using var rendered = Render(icon);

        Assert.Null(icon.Data);
    }

    [AvaloniaFact]
    public void A_card_renders_its_content()
    {
        var card = new Card { Content = new TextBlock { Text = "محتوى" } };

        using var rendered = Render(card);

        Assert.Contains(rendered.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "محتوى");
    }

    [AvaloniaTheory]
    [InlineData(InfoBarSeverity.Informational, ":informational")]
    [InlineData(InfoBarSeverity.Success, ":success")]
    [InlineData(InfoBarSeverity.Warning, ":warning")]
    [InlineData(InfoBarSeverity.Error, ":error")]
    public void Each_severity_sets_its_own_pseudo_class(InfoBarSeverity severity, string expected)
    {
        var bar = new InfoBar { Severity = severity, Message = "رسالة" };

        using var rendered = Render(bar);

        Assert.Contains(expected, bar.Classes);
    }

    [AvaloniaFact]
    public void Severity_is_carried_by_a_different_icon_not_only_by_colour()
    {
        // Two severities that differ only in hue are the same message to a viewer who cannot tell those hues apart,
        // and this control carries the app's most consequential sentences.
        var symbols = new List<string?>();
        foreach (var severity in Enum.GetValues<InfoBarSeverity>())
        {
            var bar = new InfoBar { Severity = severity, Message = "رسالة" };
            using var rendered = Render(bar);
            symbols.Add(bar.GetVisualDescendants().OfType<Icon>().First().Symbol);
        }

        Assert.Equal(symbols.Count, symbols.Distinct().Count());
        Assert.DoesNotContain(null, symbols);
    }

    [AvaloniaFact]
    public void A_closed_bar_is_not_visible()
    {
        var bar = new InfoBar { IsOpen = false, Message = "رسالة" };

        using var rendered = Render(bar);

        Assert.False(bar.IsVisible);
    }

    [AvaloniaFact]
    public void A_title_only_takes_space_when_there_is_one()
    {
        var withoutTitle = new InfoBar { Message = "رسالة" };
        using (Render(withoutTitle))
        {
            Assert.DoesNotContain(":has-title", withoutTitle.Classes);
        }

        var withTitle = new InfoBar { Title = "عنوان", Message = "رسالة" };
        using (Render(withTitle))
        {
            Assert.Contains(":has-title", withTitle.Classes);
        }
    }
}
