using System.Windows;
using System.Windows.Input;
using Stickmark.Utils;
using Stickmark.ViewModels;

namespace Stickmark.Views;

/// Quick switcher (Ctrl+P) popup. Code-behind only calculates key routing, focus, and location — search/open logic is VM.
public partial class QuickSwitcherWindow : Window
{
    private readonly QuickSwitcherViewModel _viewModel;
    private bool _closing;

    public QuickSwitcherWindow(QuickSwitcherViewModel viewModel)
    {
        InitializeComponent();
        Icon = IconFactory.CreateAppIcon();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Center of active monitor, 30% vertically — simple placement based on SystemParameters.WorkArea.
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + workArea.Height * 0.3;

        Loaded += async (_, _) =>
        {
            await _viewModel.LoadAsync().ConfigureAwait(true);
            QueryBox.Focus();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => RequestClose();
        _viewModel.CloseRequested += RequestClose;
        Closed += (_, _) => _viewModel.CloseRequested -= RequestClose;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                ScrollSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                ScrollSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                RequestClose();
                e.Handled = true;
                break;
        }
    }

    private void ScrollSelectedIntoView()
    {
        if (ResultsList.SelectedItem is not null) ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void ResultRow_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuickSwitcherItem item })
        {
            _viewModel.OpenNoteCommand.Execute(item);
        }
    }

    // Both Deactivated (leaving focus) and VM's CloseRequested (opening selection) can close this window, preventing duplicate Close().
    private void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
