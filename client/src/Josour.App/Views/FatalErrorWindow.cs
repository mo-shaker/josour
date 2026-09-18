using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Josour.App.Views;

/// <summary>
/// The one message the app shows when start-up failed, before any of its windows or view models exist.
/// <para>
/// Built in code rather than XAML on purpose: this runs when something in the composition root has already thrown, and
/// a failure window that needs the resource dictionaries, the theme and the XAML loader to be healthy is a failure
/// window that will not appear on the one occasion it is for.
/// </para>
/// </summary>
public static class FatalErrorWindow
{
    /// <summary>Shows the message. Never throws: there is nothing left to fall back to.</summary>
    public static void Show(string title, string message)
    {
        try
        {
            var window = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                FlowDirection = UiFlow.Direction,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(24),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontSize = 18,
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };

            window.Show();
        }
        catch (Exception)
        {
            // The log already has the real failure; a window that cannot open must not replace it with its own.
        }
    }
}
