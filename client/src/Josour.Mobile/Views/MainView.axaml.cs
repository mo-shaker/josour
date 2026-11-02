using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Josour.Infrastructure.Localization;

namespace Josour.Mobile.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
        FlowDirection = LocalizedStrings.Current.IsRightToLeft
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;
    }
}
