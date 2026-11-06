using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Josour.Brand.Controls;

/// <summary>
/// The lock-up for the interface language (<c>lockup-ar-first-rtl.svg</c> in Arabic, <c>lockup-en-first-ltr.svg</c> in English),
/// drawn from <see cref="BrandGeometry"/> at the files' own proportions. The colours are the theme's logo colours: teal and
/// ink as supplied in light mode, the approved one-colour white in dark mode. It never mirrors.
/// </summary>
public sealed class BrandLockup : Control
{
    public static readonly StyledProperty<IBrush?> SymbolBrushProperty = AvaloniaProperty.Register<BrandLockup, IBrush?>(nameof(SymbolBrush));
    public static readonly StyledProperty<IBrush?> WordmarkBrushProperty = AvaloniaProperty.Register<BrandLockup, IBrush?>(nameof(WordmarkBrush));

    private static readonly Lazy<(Geometry[] Wordmark, Geometry Symbol)> En = new(() => Build(BrandGeometry.EnglishFirst));
    private static readonly Lazy<(Geometry[] Wordmark, Geometry Symbol)> Ar = new(() => Build(BrandGeometry.ArabicFirst));

    static BrandLockup() => AffectsRender<BrandLockup>(SymbolBrushProperty, WordmarkBrushProperty);

    public IBrush? SymbolBrush { get => GetValue(SymbolBrushProperty); set => SetValue(SymbolBrushProperty, value); }
    public IBrush? WordmarkBrush { get => GetValue(WordmarkBrushProperty); set => SetValue(WordmarkBrushProperty, value); }

    protected override bool BypassFlowDirectionPolicies => true;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(Width) ? Width : double.IsFinite(availableSize.Width) ? availableSize.Width : LockupGeometry.Width;
        return new Size(width, width * LockupGeometry.Height / LockupGeometry.Width);
    }

    public override void Render(DrawingContext context)
    {
        // The interface language shows in the inherited reading direction: Arabic is right to left.
        var (wordmark, symbol) = FlowDirection == FlowDirection.RightToLeft ? Ar.Value : En.Value;
        var scale = Math.Min(Bounds.Width / LockupGeometry.Width, Bounds.Height / LockupGeometry.Height);
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale));
        foreach (var part in wordmark)
        {
            context.DrawGeometry(WordmarkBrush, null, part);
        }

        context.DrawGeometry(SymbolBrush, null, symbol);
    }

    private static (Geometry[], Geometry) Build(LockupGeometry lockup)
    {
        var symbol = Geometry.Parse(BrandGeometry.Symbol);
        symbol.Transform = new MatrixTransform(Matrix.CreateScale(lockup.SymbolScale, lockup.SymbolScale) * Matrix.CreateTranslation(lockup.SymbolX, lockup.SymbolY));
        return (lockup.Wordmark.Select(Geometry.Parse).ToArray(), symbol);
    }
}

/// <summary>
/// The splash badge: a brand-teal disc carrying the white symbol, the same in both themes, drawn where the Android splash
/// draws it so the hand-over from the system splash to the "Starting" screen does not move.
/// </summary>
public sealed class SymbolBadge : Control
{
    public static readonly StyledProperty<IBrush?> DiscBrushProperty = AvaloniaProperty.Register<SymbolBadge, IBrush?>(nameof(DiscBrush));
    public static readonly StyledProperty<IBrush?> SymbolBrushProperty = AvaloniaProperty.Register<SymbolBadge, IBrush?>(nameof(SymbolBrush));

    private static readonly Lazy<Geometry> Symbol = new(() => Geometry.Parse(BrandGeometry.Symbol));

    static SymbolBadge() => AffectsRender<SymbolBadge>(DiscBrushProperty, SymbolBrushProperty);

    public IBrush? DiscBrush { get => GetValue(DiscBrushProperty); set => SetValue(DiscBrushProperty, value); }
    public IBrush? SymbolBrush { get => GetValue(SymbolBrushProperty); set => SetValue(SymbolBrushProperty, value); }

    protected override bool BypassFlowDirectionPolicies => true;

    public override void Render(DrawingContext context)
    {
        var d = Math.Min(Bounds.Width, Bounds.Height);
        if (d <= 0) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(DiscBrush, null, centre, d / 2, d / 2);
        // The symbol is 70/104 of the disc wide, centred on its bounding box (x 2.25..21.75, y 6..22.5): splash_icon.xml.
        var scale = d * 70 / 104 / 19.5;
        var m = Matrix.CreateTranslation(-12, -14.25) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(centre.X, centre.Y);
        using var _ = context.PushTransform(m);
        context.DrawGeometry(SymbolBrush, null, Symbol.Value);
    }
}
