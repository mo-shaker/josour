using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Josour.Mobile.ViewModels;

namespace Josour.Mobile.Controls;

/// <summary>
/// The arch as a motif (docs/design/mobile-phase1-foundation.html, "The arch as a motif"): the symbol's own geometry drawn as
/// a 3-unit band of radius 7.5 about (12, 15) and two dots of radius 2.25, in the state the status hero, the empty contacts
/// list or the request screen needs. The dot at the reading start is "you": in a right-to-left layout the whole drawing is
/// mirrored by the layout, which moves only that meaning, since the shape is symmetric.
/// <para>Motion is a stroke offset only (<see cref="Progress"/>, animated by the styles) and stops when <see cref="MobileTheme.ReduceMotion"/> is set.</para>
/// </summary>
public sealed class Motif : Control
{
    public static readonly StyledProperty<MotifState> StateProperty = AvaloniaProperty.Register<Motif, MotifState>(nameof(State));

    /// <summary>0..1, animated by the styles for <see cref="MotifState.Connecting"/> and <see cref="MotifState.Session"/>.</summary>
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<Motif, double>(nameof(Progress), 1d);

    public static readonly StyledProperty<IBrush?> ActiveBrushProperty = AvaloniaProperty.Register<Motif, IBrush?>(nameof(ActiveBrush));
    public static readonly StyledProperty<IBrush?> IdleBrushProperty = AvaloniaProperty.Register<Motif, IBrush?>(nameof(IdleBrush));
    public static readonly StyledProperty<IBrush?> DotOffBrushProperty = AvaloniaProperty.Register<Motif, IBrush?>(nameof(DotOffBrush));
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty = AvaloniaProperty.Register<Motif, IBrush?>(nameof(HighlightBrush));

    private const double ArcLength = Math.PI * 7.5;
    // Parsed on first draw: geometry needs the platform, which does not exist yet when the Android head starts.
    private static readonly Lazy<Geometry> ArcShape = new(() => Geometry.Parse("M4.5,15 A7.5,7.5 0 0,1 19.5,15"));
    private static readonly Lazy<Geometry> BrokenShape = new(() => Geometry.Parse("M4.5,15 A7.5,7.5 0 0,1 9.435,7.952 M14.565,7.952 A7.5,7.5 0 0,1 19.5,15"));

    private static Geometry Arc => ArcShape.Value;
    private static Geometry Broken => BrokenShape.Value;

    static Motif()
    {
        AffectsRender<Motif>(StateProperty, ProgressProperty, ActiveBrushProperty, IdleBrushProperty, DotOffBrushProperty, HighlightBrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<Motif>(false);
    }

    /// <summary>The system's "remove animations" setting, as the Android head read it.</summary>
    private static bool ReduceMotion => MobileTheme.ReduceMotion;

    public MotifState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public IBrush? ActiveBrush { get => GetValue(ActiveBrushProperty); set => SetValue(ActiveBrushProperty, value); }
    public IBrush? IdleBrush { get => GetValue(IdleBrushProperty); set => SetValue(IdleBrushProperty, value); }
    public IBrush? DotOffBrush { get => GetValue(DotOffBrushProperty); set => SetValue(DotOffBrushProperty, value); }
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var active = ActiveBrush ?? Brushes.Teal;
        var idle = IdleBrush ?? Brushes.LightGray;
        var off = DotOffBrush ?? Brushes.Gray;
        var progress = ReduceMotion ? 1 : Math.Clamp(Progress, 0, 1);

        using var _ = context.PushTransform(Matrix.CreateTranslation((Bounds.Width - size) / 2 / (size / 24), (Bounds.Height - size) / 2 / (size / 24)) * Matrix.CreateScale(size / 24, size / 24));
        var band = new Pen(idle, 3, lineCap: PenLineCap.Flat);
        var bandOn = new Pen(active, 3, lineCap: PenLineCap.Flat);

        // (you, other): filled active, filled idle, or an open ring.
        (Dot You, Dot Other) dots;
        switch (State)
        {
            case MotifState.Off:
            case MotifState.Empty:
                context.DrawGeometry(null, band, Arc);
                dots = (Dot.Open, Dot.Open);
                break;
            case MotifState.Connecting:
                context.DrawGeometry(null, band, Arc);
                var drawn = ReduceMotion ? 0.35 : progress;
                if (drawn > 0)
                {
                    context.DrawGeometry(null, new Pen(active, 3, lineCap: PenLineCap.Flat) { DashStyle = new DashStyle(new[] { drawn * ArcLength / 3, 100 }, 0) }, Arc);
                }

                dots = (Dot.Active, Dot.Open);
                break;
            case MotifState.Ready:
                context.DrawGeometry(null, bandOn, Arc);
                dots = (Dot.Active, Dot.Open);
                break;
            case MotifState.Unreachable:
                context.DrawGeometry(null, band, Broken);
                dots = (Dot.Idle, Dot.Open);
                break;
            case MotifState.Attention:
                context.DrawGeometry(null, band, Arc);
                dots = (Dot.Idle, Dot.Open);
                break;
            case MotifState.Session:
                context.DrawGeometry(null, bandOn, Arc);
                if (!ReduceMotion && HighlightBrush is { } light)
                {
                    // A short lighter segment travelling along the arch: the connection is in use.
                    var offset = (1 - progress) * (ArcLength + 3) - 3;
                    context.DrawGeometry(null, new Pen(light, 3, lineCap: PenLineCap.Flat) { DashStyle = new DashStyle(new[] { 1.0, 100 }, -offset / 3) }, Arc);
                }

                dots = (Dot.Active, Dot.Active);
                break;
            case MotifState.Ended:
                context.DrawGeometry(null, band, Arc);
                dots = (Dot.Active, Dot.Idle);
                break;
            case MotifState.Offer:
                context.DrawGeometry(null, new Pen(active, 3, lineCap: PenLineCap.Flat) { DashStyle = new DashStyle(new[] { 0.73, 0.53 }, 0) }, Arc);
                dots = (Dot.Active, Dot.Active);
                break;
            default:
                return;
        }

        DrawDot(context, 4.5, dots.You, active, idle, off);
        DrawDot(context, 19.5, dots.Other, active, idle, off);
    }

    private static void DrawDot(DrawingContext context, double x, Dot dot, IBrush active, IBrush idle, IBrush off)
    {
        var centre = new Point(x, 20.25);
        switch (dot)
        {
            case Dot.Active: context.DrawEllipse(active, null, centre, 2.25, 2.25); break;
            case Dot.Idle: context.DrawEllipse(idle, null, centre, 2.25, 2.25); break;
            default: context.DrawEllipse(null, new Pen(off, 1.2), centre, 1.65, 1.65); break;
        }
    }

    private enum Dot { Active, Idle, Open }
}
