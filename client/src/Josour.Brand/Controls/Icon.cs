using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Josour.Brand.Controls;

/// <summary>The UI icons of both apps (docs/design/mobile-phase1-foundation.html, "UI icons"; the desktop's additions in desktop-phase2-mockups.html).</summary>
public enum IconKind
{
    None, Info, Warning, Danger, Check, Ok, Close, Wifi, Cellular, NoNetwork, NotificationsOff, Battery, Clock, PersonAdd, User,
    Stop, SignOut, Data, Share, People, ChevronDown, Server, Pin, Language, Mail, Link, Laptop,
    More, Settings, Eye, Copy, Folder, Refresh, Search, Key, Unlock, ChevronEnd,
}

/// <summary>
/// One icon on the 24 dp grid: 2 dp strokes with round caps and joins, and solid dots of the symbol's own 2.25 radius where
/// an icon needs a point. It draws in its inherited <see cref="Foreground"/>, so an icon takes the colour of the text it sits
/// with. Directional icons (sign out, chevrons) mirror in right-to-left layouts; every other icon never does.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<IconKind> KindProperty = AvaloniaProperty.Register<Icon, IconKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    // Built on first draw: geometry needs the platform, which does not exist yet when the Android head starts.
    private static readonly Lazy<Dictionary<IconKind, (Geometry? Stroke, Geometry? Fill, bool Directional)>> ShapeTable = new(BuildShapes);

    private static Dictionary<IconKind, (Geometry? Stroke, Geometry? Fill, bool Directional)> BuildShapes() => new()
    {
        [IconKind.Info] = (G("M12,3.5A8.5,8.5 0 1,1 11.99,3.5Z M12,11V16.5"), Dot(12, 7.75, 1.25), false),
        [IconKind.Warning] = (G("M12,3.5L21.5,20H2.5Z M12,10V14.5"), Dot(12, 17.25, 1.25), false),
        [IconKind.Danger] = (G("M8.5,3H15.5L21,8.5V15.5L15.5,21H8.5L3,15.5V8.5Z M12,7.5V13.5"), Dot(12, 16.75, 1.25), false),
        [IconKind.Check] = (G("M12,3.5A8.5,8.5 0 1,1 11.99,3.5Z M8.25,12.25L10.75,14.75L15.75,9.75"), null, false),
        [IconKind.Ok] = (G("M5,12.5L9.5,17L19,7.5"), null, false),
        [IconKind.Close] = (G("M6.5,6.5L17.5,17.5 M17.5,6.5L6.5,17.5"), null, false),
        [IconKind.Wifi] = (G("M3.5,9.5A12,12 0 0,1 20.5,9.5 M6.75,12.75A7.5,7.5 0 0,1 17.25,12.75"), Dot(12, 17.25, 2.25), false),
        [IconKind.Cellular] = (G("M5,19V16 M9.67,19V13 M14.33,19V10 M19,19V6"), null, false),
        [IconKind.NoNetwork] = (G("M3.5,9.5A12,12 0 0,1 12.9,6.03 M17.6,7.5A12,12 0 0,1 20.5,9.5 M6.75,12.75A7.5,7.5 0 0,1 10.75,10.65 M4,4L20,20"), Dot(12, 17.25, 2.25), false),
        [IconKind.NotificationsOff] = (G("M8.2,5.6A4.5,4.5 0 0,1 16.5,8.5V12.5L18.5,16H9 M7.5,9.5V12.5L5.5,16H8.5 M10,19A2,2 0 0,0 14,19 M4,4L20,20"), null, false),
        [IconKind.Battery] = (G("M5,7.5H16.5A2,2 0 0,1 18.5,9.5V14.5A2,2 0 0,1 16.5,16.5H5A2,2 0 0,1 3,14.5V9.5A2,2 0 0,1 5,7.5Z M21,10.75V13.25 M6.5,11V13"), null, false),
        [IconKind.Clock] = (G("M12,3.5A8.5,8.5 0 1,1 11.99,3.5Z M12,7.5V12L15,14"), null, false),
        [IconKind.PersonAdd] = (G("M10,5A3.5,3.5 0 1,1 9.99,5Z M3.5,19.5A6.5,6.5 0 0,1 16.5,19.5 M19,8.5V14.5 M16,11.5H22"), null, false),
        [IconKind.User] = (G("M12,4.75A3.75,3.75 0 1,1 11.99,4.75Z M5,20A7,7 0 0,1 19,20"), null, false),
        [IconKind.Stop] = (G("M12,3.5A8.5,8.5 0 1,1 11.99,3.5Z"), G("M10,9H14A1,1 0 0,1 15,10V14A1,1 0 0,1 14,15H10A1,1 0 0,1 9,14V10A1,1 0 0,1 10,9Z"), false),
        [IconKind.SignOut] = (G("M13.5,4H18A2,2 0 0,1 20,6V18A2,2 0 0,1 18,20H13.5 M8.5,8L4.5,12L8.5,16 M4.5,12H14.5"), null, true),
        [IconKind.Data] = (G("M8,4V19 M4.5,15.5L8,19L11.5,15.5 M16,20V5 M12.5,8.5L16,5L19.5,8.5"), null, false),
        [IconKind.Share] = (G("M4.5,15A7.5,7.5 0 0,1 19.5,15"), Combine(Dot(4.5, 19.5, 1.75), Dot(19.5, 19.5, 1.75)), false),
        [IconKind.People] = (G("M9,5.25A3.25,3.25 0 1,1 8.99,5.25Z M3,19A6,6 0 0,1 15,19 M15.5,5.6A3.25,3.25 0 0,1 15.5,11.4 M18,14.2A6,6 0 0,1 21,19"), null, false),
        [IconKind.ChevronDown] = (G("M6,9.5L12,15.5L18,9.5"), null, false),
        [IconKind.Server] = (G("M6,4H18A2,2 0 0,1 20,6V9A2,2 0 0,1 18,11H6A2,2 0 0,1 4,9V6A2,2 0 0,1 6,4Z M6,13H18A2,2 0 0,1 20,15V18A2,2 0 0,1 18,20H6A2,2 0 0,1 4,18V15A2,2 0 0,1 6,13Z"), Combine(Dot(8, 7.5, 1), Dot(8, 16.5, 1)), false),
        [IconKind.Pin] = (G("M12,21C12,21 5.5,15.4 5.5,10A6.5,6.5 0 0,1 18.5,10C18.5,15.4 12,21 12,21Z"), Dot(12, 10, 2.25), false),
        [IconKind.Language] = (G("M3.5,6H12.5 M8,4V6C8,10 5.8,13.5 3.5,15 M5.5,10.5C6.9,12.5 8.9,13.9 11.5,14.5 M12.5,20L16.5,11L20.5,20 M14,17H19"), null, false),
        [IconKind.Mail] = (G("M5,5.5H19A2,2 0 0,1 21,7.5V16.5A2,2 0 0,1 19,18.5H5A2,2 0 0,1 3,16.5V7.5A2,2 0 0,1 5,5.5Z M3.5,7L12,13L20.5,7"), null, false),
        [IconKind.Link] = (G("M10,14A4,4 0 0,0 15.66,14L18.66,11A4,4 0 0,0 13,5.34L12,6.34 M14,10A4,4 0 0,0 8.34,10L5.34,13A4,4 0 0,0 11,18.66L12,17.66"), null, false),
        [IconKind.More] = (null, Combine(Combine(Dot(5.5, 12, 1.75), Dot(12, 12, 1.75)), Dot(18.5, 12, 1.75)), false),
        [IconKind.Settings] = (G("M4,7H12.5 M17.5,7H20 M4,17H6.5 M11.5,17H20 M15,4.5A2.5,2.5 0 1,1 14.99,4.5Z M9,14.5A2.5,2.5 0 1,1 8.99,14.5Z"), null, false),
        [IconKind.Eye] = (G("M2.5,12C5,7.5 8.5,5.5 12,5.5C15.5,5.5 19,7.5 21.5,12C19,16.5 15.5,18.5 12,18.5C8.5,18.5 5,16.5 2.5,12Z M12,9A3,3 0 1,1 11.99,9Z"), null, false),
        [IconKind.Copy] = (G("M9,8H18A1.5,1.5 0 0,1 19.5,9.5V19A1.5,1.5 0 0,1 18,20.5H9A1.5,1.5 0 0,1 7.5,19V9.5A1.5,1.5 0 0,1 9,8Z M4.5,15.5V5A1.5,1.5 0 0,1 6,3.5H15"), null, false),
        [IconKind.Folder] = (G("M5,6H9.5L11.5,8H19A1.5,1.5 0 0,1 20.5,9.5V17.5A1.5,1.5 0 0,1 19,19H5A1.5,1.5 0 0,1 3.5,17.5V7.5A1.5,1.5 0 0,1 5,6Z"), null, false),
        [IconKind.Refresh] = (G("M19.5,12A7.5,7.5 0 1,1 17.3,6.7 M18,3.5V7H14.5"), null, false),
        [IconKind.Search] = (G("M10.5,4.5A6,6 0 1,1 10.49,4.5Z M15,15L20,20"), null, false),
        [IconKind.Key] = (G("M8,9.5A4,4 0 1,1 7.99,9.5Z M11.5,13.5H20.5V16.5 M17.5,13.5V16"), null, false),
        [IconKind.Unlock] = (G("M7,11V8A5,5 0 0,1 16.6,6 M6,11H18A1.5,1.5 0 0,1 19.5,12.5V19A1.5,1.5 0 0,1 18,20.5H6A1.5,1.5 0 0,1 4.5,19V12.5A1.5,1.5 0 0,1 6,11Z"), null, false),
        [IconKind.ChevronEnd] = (G("M9.5,6L15.5,12L9.5,18"), null, true),
        [IconKind.Laptop] = (G("M6.5,5H17.5A1.5,1.5 0 0,1 19,6.5V13.5A1.5,1.5 0 0,1 17.5,15H6.5A1.5,1.5 0 0,1 5,13.5V6.5A1.5,1.5 0 0,1 6.5,5Z M2.5,19H21.5"), null, false),
    };

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty);
        FlowDirectionProperty.Changed.AddClassHandler<Icon>((icon, _) => icon.InvalidateVisual());
        WidthProperty.OverrideDefaultValue<Icon>(24);
        HeightProperty.OverrideDefaultValue<Icon>(24);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
    }

    public IconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Drawn the right way round whatever the layout direction; <see cref="Render"/> mirrors directional icons itself.</summary>
    protected override bool BypassFlowDirectionPolicies => true;

    public override void Render(DrawingContext context)
    {
        if (!ShapeTable.Value.TryGetValue(Kind, out var shape) || Foreground is not { } brush || Bounds.Width <= 0)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var transform = Matrix.CreateScale(scale, scale);
        if (shape.Directional && FlowDirection == FlowDirection.RightToLeft)
        {
            transform = Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(24, 0) * transform;
        }

        using (context.PushTransform(transform))
        {
            if (shape.Stroke is { } stroke)
            {
                context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), stroke);
            }

            if (shape.Fill is { } fill)
            {
                context.DrawGeometry(brush, null, fill);
            }
        }
    }

    private static Geometry G(string data) => Geometry.Parse(data);

    private static Geometry Dot(double x, double y, double r) => new EllipseGeometry(new Rect(x - r, y - r, 2 * r, 2 * r));

    private static Geometry Combine(Geometry a, Geometry b) => new GeometryGroup { Children = { a, b } };
}
