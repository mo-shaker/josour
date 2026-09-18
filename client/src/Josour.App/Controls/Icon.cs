using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Josour.App.Controls;

/// <summary>
/// One Fluent glyph, addressed by name (<c>&lt;c:Icon Symbol="Globe" /&gt;</c>) rather than by resource key.
/// <para>
/// The indirection is what stops seventeen call sites from each deciding a size and a brush. A missing name draws
/// nothing rather than throwing: an icon is decoration, and a typo in one must not take a window down.
/// </para>
/// </summary>
public sealed class Icon : TemplatedControl
{
    public static readonly StyledProperty<string?> SymbolProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Symbol));

    /// <summary>The key suffix in <c>Controls/Icons.axaml</c>: <c>Globe</c> resolves <c>IconGlobe</c>.</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    static Icon()
    {
        SymbolProperty.Changed.AddClassHandler<Icon>((icon, _) => icon.Resolve());
    }

    public string? Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>The resolved geometry; set by <see cref="Symbol"/> and read by the template.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Resources are only reachable once the control is in a tree, so a Symbol set in XAML resolves here.
        Resolve();
    }

    private void Resolve()
    {
        if (string.IsNullOrEmpty(Symbol))
        {
            Data = null;
            return;
        }

        if (this.TryFindResource("Icon" + Symbol, out var resource) && resource is Geometry geometry)
        {
            Data = geometry;
        }
    }
}
