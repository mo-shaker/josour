using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Josour.Infrastructure.Localization;
using Josour.Mobile.ViewModels;

namespace Josour.Mobile.Views;

public partial class MainView : UserControl
{
    private TopLevel? _topLevel;

    public MainView()
    {
        var arabic = LocalizedStrings.Current.IsRightToLeft;
        // The type scale for this language and the phone's font size, before the views read it.
        MobileTheme.ApplyTypography(Resources, arabic, MobileTheme.FontScale);
        // Addresses, emails and passwords stay left to right, aligned with their label's side.
        Resources["Josour.LtrFieldAlignment"] = arabic ? TextAlignment.Right : TextAlignment.Left;
        AvaloniaXamlLoader.Load(this);
        FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.BackRequested += OnBackRequested;
            _topLevel.ActualThemeVariantChanged += OnThemeChanged;
        }

        ColourSystemBars();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            _topLevel.ActualThemeVariantChanged -= OnThemeChanged;
            _topLevel = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ColourSystemBars();

    /// <summary>The status and navigation bars take the canvas colour, so the screen is one surface in either theme.</summary>
    private void ColourSystemBars()
    {
        if (_topLevel?.InsetsManager is { } insets && this.TryFindResource("Josour.Color.Canvas", _topLevel.ActualThemeVariant, out var value) && value is Color canvas)
        {
            insets.SystemBarColor = canvas;
        }
    }

    /// <summary>Back closes a sheet, then returns from Contacts to Share; otherwise Android's own back applies.</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (main.IsConfirmingSignOut)
        {
            main.CancelSignOutCommand.Execute(null);
            e.Handled = true;
        }
        else if (main.Contacts.TrustCandidate is { } candidate)
        {
            candidate.CancelTrustCommand.Execute(null);
            e.Handled = true;
        }
        else if (main.IsContactsPage && !main.Host.HasIncoming)
        {
            main.ShowHost();
            e.Handled = true;
        }
    }
}
