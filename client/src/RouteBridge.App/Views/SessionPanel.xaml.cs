using System.Windows.Controls;

namespace RouteBridge.App.Views;

/// <summary>The live session view. DataContext is <see cref="ViewModels.SessionPanelViewModel"/> (bound by MainWindow).</summary>
public partial class SessionPanel : UserControl
{
    public SessionPanel()
    {
        InitializeComponent();
    }
}
