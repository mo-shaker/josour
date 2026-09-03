using System.ComponentModel;
using RouteBridge.App.ViewModels;
using Wpf.Ui.Controls;

namespace RouteBridge.App.Views;

/// <summary>
/// Top-most prompt shown next to the toast (Focus Assist can suppress toasts). Closes itself once the ViewModel has a decision;
/// closing it manually counts as "dismissed".
/// </summary>
public partial class IncomingRequestWindow : FluentWindow
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
        if (Dispatcher.CheckAccess())
        {
            Close();
        }
        else
        {
            Dispatcher.InvokeAsync(Close);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
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
