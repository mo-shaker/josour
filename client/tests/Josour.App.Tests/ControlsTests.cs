using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Josour.App.Controls;
using Josour.Brand.Controls;

namespace Josour.App.Tests;

/// <summary>
/// The desktop's own controls on the tokens: <see cref="Card"/>, <see cref="InfoBar"/> and <see cref="PlatformButtonRow"/>,
/// and the brand <see cref="Icon"/> they draw with. They are the pieces with no library behind them.
/// </summary>
public class ControlsTests
{
    private static Rendered Render(Control content) => new(content, width: 200, height: 200);

    /// <summary>Every icon kind the desktop's views and banners use draws something; an unknown kind draws nothing.</summary>
    [AvaloniaFact]
    public void Every_icon_the_views_use_has_a_shape()
    {
        var views = Directory.GetFiles(Path.Combine(RepoRoot(), "client/src/Josour.App"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
        var kinds = views.SelectMany(v => System.Text.RegularExpressions.Regex.Matches(v, "Kind=\"(\\w+)\"").Select(m => m.Groups[1].Value))
            .Concat(new[] { "Info", "Check", "Warning", "Danger" })
            .Distinct()
            .ToList();

        Assert.NotEmpty(kinds);
        foreach (var name in kinds)
        {
            Assert.True(Enum.TryParse<IconKind>(name, out var kind), name);
            var icon = new Icon { Kind = kind, Width = 24, Height = 24, Foreground = Brushes.Black, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            using var rendered = Render(icon);
            var frame = rendered.Window.CaptureRenderedFrame()!;
            Assert.True(HasInk(frame), name + " draws nothing");
        }
    }

    private static bool HasInk(Avalonia.Media.Imaging.WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        // The window's canvas is near white in the light theme; any dark pixel in the top-left 24x24 is the icon.
        for (var y = 0; y < 24; y++)
        {
            for (var x = 0; x < 24; x++)
            {
                var i = y * buffer.RowBytes + x * 4;
                if (bytes[i] < 128 && bytes[i + 1] < 128 && bytes[i + 2] < 128)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "client", "Josour.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repository root not found");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dialog_buttons_follow_each_system_s_order(bool mac)
    {
        // Phase 1 decision 9: Windows puts the primary first, macOS last.
        var before = PlatformButtonRow.UseMacOrder;
        PlatformButtonRow.UseMacOrder = mac;
        try
        {
            var primary = new Button { Content = "save" };
            primary.Classes.Add("primary");
            var row = new PlatformButtonRow { Children = { new Button { Content = "cancel" }, primary } };
            using var rendered = Render(row);

            Assert.Equal(mac ? 1 : 0, row.Children.IndexOf(primary));
        }
        finally
        {
            PlatformButtonRow.UseMacOrder = before;
        }
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
            symbols.Add(bar.GetVisualDescendants().OfType<Icon>().First().Kind.ToString());
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
