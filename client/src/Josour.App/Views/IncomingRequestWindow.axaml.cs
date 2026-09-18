using Avalonia.Controls;
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
    }

    private void OnCompleted(object? sender, EventArgs e)
    {
        _viewModel.Completed -= OnCompleted;
        UiThread.Post(Close);
    }

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
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
