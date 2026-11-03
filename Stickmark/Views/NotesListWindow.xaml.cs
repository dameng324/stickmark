using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Stickmark.Services;
using Stickmark.Utils;
using Stickmark.ViewModels;

namespace Stickmark.Views;

public partial class NotesListWindow : Window
{
    private readonly NotesListViewModel _viewModel;

    public NotesListWindow(NotesListViewModel viewModel)
    {
        InitializeComponent();
        Icon = IconFactory.CreateAppIcon();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => { await _viewModel.ReloadAsync(); SearchBox.Focus(); };
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Hide instead of close so we don't have to rebuild the VM each time the user toggles the list.
        e.Cancel = true;
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            App.Services.GetRequiredService<IWindowManager>().OpenQuickSwitcher();
            e.Handled = true;
        }
    }

    private void Card_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Clicking on the selection checkbox does not open the note (only toggles selection).
        if (e.OriginalSource is DependencyObject src && FindAncestor<ToggleButton>(src) is not null) return;

        if (sender is FrameworkElement fe && fe.DataContext is NoteSummary summary)
        {
            // Opening the trash card by clicking on it is ignored because the intention is ambiguous — only the restore/permanent delete button is received.
            if (summary.IsTrashed) return;
            _viewModel.OpenCommand.Execute(summary);
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = start; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is T match) return match;
        }
        return null;
    }

    private void ActiveTab_OnClick(object sender, RoutedEventArgs e)
    {
        // Active tab is a one-way toggle (if it's on and pressed again, it stays on). IsChecked in XAML
        // Because it is OneWay binding, ShowTrash is turned off directly.
        _viewModel.ShowTrash = false;
    }

    private void TodosTab_OnClick(object sender, RoutedEventArgs e)
    {
        // The to-do tab also has a one-way toggle pattern like the active tab (IsChecked is OneWay).
        _viewModel.ViewMode = NoteListViewMode.Todos;
    }

    private void TodoGroupHeader_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TodoGroup group)
        {
            _viewModel.OpenTodoNoteCommand.Execute(group.NoteId);
        }
    }

    private void TodoItemText_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TodoItemViewModel item)
        {
            _viewModel.OpenTodoNoteCommand.Execute(item.NoteId);
        }
    }

    public async System.Threading.Tasks.Task ShowAndReloadAsync()
    {
        await _viewModel.ReloadAsync().ConfigureAwait(true);
        if (!IsVisible) Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// Enter tray ‘To-do view’ — Opens the window with the To-do tab.
    public async System.Threading.Tasks.Task ShowTodoTabAsync()
    {
        _viewModel.ViewMode = NoteListViewMode.Todos;
        await ShowAndReloadAsync().ConfigureAwait(true);
    }
}
