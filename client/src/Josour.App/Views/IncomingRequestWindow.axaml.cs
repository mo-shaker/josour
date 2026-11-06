using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Josour.App.Services;
using Josour.App.ViewModels;

namespace Josour.App.Views;

/// <summary>
/// Top-most prompt shown next to the toast (Focus Assist can suppress toasts). Closes itself once the ViewModel has a decision;
/// closing it manually counts as "dismissed".
/// </summary>
public partial class IncomingRequestWindow : Window
{
    private readonly IncomingRequestViewModel _viewModel;

    public IncomingRequestWindow(IncomingRequestViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Completed += OnCompleted;
        viewModel.CloseRequested += OnCloseRequested;

        // Focus lands on the heading, so a screen reader starts with who is asking; Esc means "decide later".
        Opened += (_, _) => this.FindControl<TextBlock>("Heading")?.Focus();
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(Close) });
    }

    /// <summary>How long an expired request stays on screen when nobody closes it.</summary>
    public static TimeSpan ExpiredHold { get; set; } = TimeSpan.FromSeconds(10);

    private void OnCompleted(object? sender, EventArgs e)
    {
        _viewModel.Completed -= OnCompleted;
        if (_viewModel.IsExpired)
        {
            // Phase 2: an expired request says so instead of vanishing mid-sentence; it closes itself after a while.
            DispatcherTimer.RunOnce(() => UiThread.Post(Close), ExpiredHold);
            return;
        }

        UiThread.Post(Close);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => UiThread.Post(Close);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && !_viewModel.IsCompleted)
        {
            _viewModel.Dismiss();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Completed -= OnCompleted;
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
