using Avalonia;
using Avalonia.Controls;

namespace Josour.Mobile.Controls;

public enum BannerKind { Info, Warning, Danger, Success }

/// <summary>
/// A message with its meaning carried by an icon shape as well as by colour (circle-i, triangle, octagon, circle-check), so it
/// never relies on colour alone. The look is the <c>Banner</c> control theme in <c>Theme/Controls.axaml</c>.
/// </summary>
public sealed class Banner : ContentControl
{
    public static readonly StyledProperty<BannerKind> KindProperty = AvaloniaProperty.Register<Banner, BannerKind>(nameof(Kind));

    public BannerKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
