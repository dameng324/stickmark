using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Stickmark.Models;
using Stickmark.Resources;
using Stickmark.Services;
using Stickmark.Utils;
using Stickmark.ViewModels;

namespace Stickmark.Views;

public partial class NoteWindow : Window
{
    private static readonly DependencyProperty IsToolbarVisibleProperty =
        DependencyProperty.Register(nameof(IsToolbarVisible), typeof(bool), typeof(NoteWindow),
            new PropertyMetadata(false));

    private static readonly Color CodeBackground = Color.FromRgb(0xEF, 0xEF, 0xEF);
    private static readonly Color CodeBlockBackground = Color.FromRgb(0xF6, 0xF6, 0xF1);

    private readonly NoteViewModel _viewModel;
    private readonly Action<NoteWindow> _onHidden;
    private readonly Func<NoteWindow> _onNewRequested;
    private readonly Action _onListRequested;
    private readonly Func<string, bool> _onLinkActivated;
    private readonly Func<string, Task> _onOpenFileRequested;
    private bool _suppressEditorSync;
    private bool _suppressToolbarSync;
    private bool _allowClose;
    private readonly DispatcherTimer _previewTimer;

    // Monitor external changes in linked files — Since the watcher callback comes from the thread pool, it is passed to the UI thread and debounced.
    private FileSystemWatcher? _fileWatcher;
    private readonly DispatcherTimer _fileReloadTimer;
    private bool _reloadPromptOpen;

    private bool _isFullscreen;
    private bool _suppressGeometrySync;
    private bool _syncingZoom;
    private Rect _fullscreenRestoreBounds;

    // WYSIWYG (offline CodeMirror 6) editing — toggle it on in the Markdown notes.
    private MarkdownWysiwyg? _wysiwyg;
    private bool _wysiwygOn;
    private bool _wysiwygEntering;

    public NoteViewModel ViewModel => _viewModel;
    public IReadOnlyList<double> FontSizeChoices => NoteViewModel.FontSizeCatalog;

    /// Set to true when WindowManager opens a .md file — when loaded, a rendered preview is shown first.
    public bool OpenInPreview { get; set; }

    public bool IsToolbarVisible
    {
        get => (bool)GetValue(IsToolbarVisibleProperty);
        private set => SetValue(IsToolbarVisibleProperty, value);
    }

    public NoteWindow(
        NoteViewModel viewModel,
        Action<NoteWindow> onHidden,
        Func<NoteWindow> onNewRequested,
        Action onListRequested,
        Func<string, bool> onLinkActivated,
        Func<string, Task> onOpenFileRequested)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _onHidden = onHidden;
        _onNewRequested = onNewRequested;
        _onListRequested = onListRequested;
        _onLinkActivated = onLinkActivated;
        _onOpenFileRequested = onOpenFileRequested;

        Icon = IconFactory.CreateAppIcon();
        PopulateTemplateMenu();

        DataContext = viewModel;
        Left = viewModel.X;
        Top = viewModel.Y;
        Width = viewModel.Width;
        Height = viewModel.Height;

        Loaded += OnLoaded;
        LocationChanged += (_, _) => SyncGeometry();
        SizeChanged += (_, _) => SyncGeometry();
        Closing += OnClosing;

        Activated += (_, _) => UpdateToolbarVisibility();
        Deactivated += OnWindowDeactivated;
        PreviewMouseDown += OnWindowPreviewMouseDown;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Preview.ZoomFactorChanged += (_, _) => OnWebViewZoomChanged(Preview.ZoomFactor);
        WysiwygEditor.ZoomFactorChanged += (_, _) => OnWebViewZoomChanged(WysiwygEditor.ZoomFactor);
        MouseEnter += (_, _) => UpdateToolbarVisibility();
        MouseLeave += (_, _) => UpdateToolbarVisibility();
        // Lower WebView2 memory usage for notes hidden in the tray (restore when visible again) — Best-effort.
        IsVisibleChanged += (_, e) => SetWebViewMemoryLevel(low: !(bool)e.NewValue);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NoteViewModel.IsPreviewMode))
            {
                if (_wysiwygOn) return;   // Prevent the preview toggle from touching the layout during WYSIWYG editing.
                UpdateToolbarVisibility();
                UpdateContentView();
            }
            else if (args.PropertyName == nameof(NoteViewModel.Color) && _viewModel.IsPreviewMode)
            {
                RenderPreview();
            }
        };

        InputBindings.Add(new KeyBinding(
            new RelayCommandImpl(_ => ToggleStrikethrough()),
            new KeyGesture(Key.X, ModifierKeys.Control | ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(
            new RelayCommandImpl(_ => _ = ShowPreviewAsync()),
            new KeyGesture(Key.E, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayCommandImpl(_ => ToggleFullscreen()),
            new KeyGesture(Key.F11)));
        InputBindings.Add(new KeyBinding(
            new RelayCommandImpl(_ => ToggleInlineCode()),
            new KeyGesture(Key.OemTilde, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayCommandImpl(_ => OpenFileViaDialog()),
            new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayCommandImpl(_ => App.Services.GetRequiredService<IWindowManager>().OpenQuickSwitcher()),
            new KeyGesture(Key.P, ModifierKeys.Control)));

        // In Markdown/HTML mode the editor is a plain source view — force paste to plain text
        // so pasted tags/markup are kept literally instead of being converted to rich text.
        DataObject.AddPastingHandler(Editor, OnEditorPasting);

        // Debounced live re-render for the split preview.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            if (Preview.Visibility == Visibility.Visible) RenderPreview();
        };

        // If the linked file changes externally (because the editor sends out multiple events), it is collected briefly and reflected only once.
        _fileReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _fileReloadTimer.Tick += (_, _) => { _fileReloadTimer.Stop(); _ = ReloadLinkedFileAsync(); };

        Closed += (_, _) => DisposeFileWatcher();
    }

    private void OnEditorPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!IsMarkup) return; // rich mode keeps normal (formatted) paste

        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
            var plain = new DataObject();
            plain.SetData(DataFormats.UnicodeText, text);
            e.DataObject = plain;
            e.FormatToApply = DataFormats.UnicodeText;
        }
        else
        {
            // No text form (e.g. an image) — block it rather than embedding rich content.
            e.CancelCommand();
        }
    }

    public void RequestClose()
    {
        _allowClose = true;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadEditorContent();
        if (!IsMarkup) DecorateLinks();
        // Every note opens in the read-only Eye state. Double-clicking the rendered text enters WYSIWYG edit mode.
        _viewModel.IsPreviewMode = true;
        UpdateToolbarVisibility();
        UpdateContentView();
        SyncToolbarFromSelection();
        StartFileWatcherIfLinked();
        // The file may have changed while the app was turned off, so check it once immediately after loading (since there is no local editing)
        // If the files are different, quietly adjust to the file contents — the file is the original).
        if (_viewModel.IsLinkedFile) _ = ReloadLinkedFileAsync();
    }

    private void LoadEditorContent()
    {
        _suppressEditorSync = true;
        try
        {
            Editor.Document.Blocks.Clear();
            if (string.IsNullOrEmpty(_viewModel.Content))
            {
                Editor.Document.Blocks.Add(new Paragraph(new Run(string.Empty)));
                return;
            }

            // Security: TextRange.Load is an unrestricted XAML parser, so gadgets can cause code execution.
            // If you see a risk marker that is not in a normal note, do not load it as XAML and process it as pure text.
            if (_viewModel.Format == NoteContentFormat.RichTextXaml
                && !TextExtraction.ContainsDangerousXaml(_viewModel.Content))
            {
                try
                {
                    var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(_viewModel.Content));
                    range.Load(ms, DataFormats.Xaml);
                    return;
                }
                catch
                {
                    // Fall through to plain-text recovery below.
                }
            }

            // Plain-text / Markdown / HTML source: one paragraph per line.
            Editor.Document.Blocks.Clear();
            foreach (var line in _viewModel.Content.Replace("\r\n", "\n").Split('\n'))
            {
                Editor.Document.Blocks.Add(new Paragraph(new Run(line)));
            }
        }
        finally
        {
            _suppressEditorSync = false;
        }
    }

    private void Editor_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorSync) return;

        // Markdown/HTML notes store the raw source as plain text (rendered on preview).
        if (IsMarkup)
        {
            _viewModel.UpdateContent(GetEditorPlainText(), _viewModel.Format);
            if (Preview.Visibility == Visibility.Visible)
            {
                _previewTimer.Stop();
                _previewTimer.Start(); // debounce the live split re-render
            }
            return;
        }

        // Re-wrap freshly-typed [[Title]] tokens as Hyperlinks before serializing — keeps the persisted
        // XAML self-contained so links work after a restart.
        DecorateLinks();

        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Xaml);
        var xaml = Encoding.UTF8.GetString(ms.ToArray());
        _viewModel.UpdateContent(xaml, NoteContentFormat.RichTextXaml);
    }

    private void Editor_OnSelectionChanged(object sender, RoutedEventArgs e) => SyncToolbarFromSelection();

    private void SyncToolbarFromSelection()
    {
        if (Editor.Selection is null) return;
        _suppressToolbarSync = true;
        try
        {
            BoldToggle.IsChecked = IsSelectionProperty(TextElement.FontWeightProperty, FontWeights.Bold);
            ItalicToggle.IsChecked = IsSelectionProperty(TextElement.FontStyleProperty, FontStyles.Italic);
            UnderlineToggle.IsChecked = SelectionHasDecoration(TextDecorations.Underline);
            StrikeToggle.IsChecked = SelectionHasDecoration(TextDecorations.Strikethrough);

            var alignment = Editor.Selection.GetPropertyValue(Block.TextAlignmentProperty);
            AlignLeftToggle.IsChecked = alignment is TextAlignment.Left;
            AlignCenterToggle.IsChecked = alignment is TextAlignment.Center;
            AlignRightToggle.IsChecked = alignment is TextAlignment.Right;

            BulletsToggle.IsChecked = HasListMarker(TextMarkerStyle.Disc);
            NumberingToggle.IsChecked = HasListMarker(TextMarkerStyle.Decimal);

            var size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty);
            FontSizeCombo.SelectedItem = size is double d ? NearestFontSize(d) : (object?)null;
        }
        finally
        {
            _suppressToolbarSync = false;
        }
    }

    private bool IsSelectionProperty(DependencyProperty property, object expected)
    {
        var value = Editor.Selection.GetPropertyValue(property);
        return value is not null && value.Equals(expected);
    }

    private bool SelectionHasDecoration(TextDecorationCollection decoration)
    {
        var value = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        if (value is not TextDecorationCollection current) return false;
        var target = decoration.First().Location;
        return current.Any(d => d.Location == target);
    }

    private bool HasListMarker(TextMarkerStyle marker)
    {
        var pointer = Editor.Selection.Start;
        var listItem = pointer.Paragraph?.Parent as ListItem;
        return listItem?.Parent is List list && list.MarkerStyle == marker;
    }

    private static double NearestFontSize(double size)
    {
        double best = NoteViewModel.FontSizeCatalog[0];
        var bestDiff = Math.Abs(size - best);
        foreach (var candidate in NoteViewModel.FontSizeCatalog)
        {
            var diff = Math.Abs(candidate - size);
            if (diff < bestDiff)
            {
                best = candidate;
                bestDiff = diff;
            }
        }
        return best;
    }

    private void SyncGeometry()
    {
        if (_suppressGeometrySync || _isFullscreen) return;
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        _viewModel.X = Left;
        _viewModel.Y = Top;
        _viewModel.Width = ActualWidth;
        _viewModel.Height = ActualHeight;
    }

    private void UpdateToolbarVisibility()
    {
        // The legacy rich-text formatting bar is disabled in the Eye/WYSIWYG-only layout.
        IsToolbarVisible = false;
    }

    private bool IsMarkup =>
        _viewModel.Format is NoteContentFormat.Markdown or NoteContentFormat.Html;

    /// Lower WebView2 renderer memory for hidden notes (restore them when visible again) — multiple notes to tray
    /// Reduces background memory when hidden. Applies only to the created WebView, even if it fails, it is harmless (hint).
    private void SetWebViewMemoryLevel(bool low)
    {
        var level = low
            ? CoreWebView2MemoryUsageTargetLevel.Low
            : CoreWebView2MemoryUsageTargetLevel.Normal;
        try { if (Preview.CoreWebView2 is { } p) p.MemoryUsageTargetLevel = level; } catch { /* best-effort */ }
        try { if (WysiwygEditor.CoreWebView2 is { } w) w.MemoryUsageTargetLevel = level; } catch { /* best-effort */ }
    }

    private string GetEditorPlainText() =>
        _viewModel.Format == NoteContentFormat.RichTextXaml
            ? new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.TrimEnd('\r', '\n')
            : _viewModel.Content;

    private async Task ShowPreviewAsync()
    {
        if (_wysiwygOn) await ExitWysiwygAsync(save: true);
        _viewModel.IsPreviewMode = true;
        UpdateContentView();
    }

    private async Task EnterWysiwygAsync()
    {
        if (_wysiwygOn || _wysiwygEntering) return;
        _wysiwygEntering = true;
        try
        {
            _wysiwyg ??= new MarkdownWysiwyg(WysiwygEditor);
            _wysiwyg.Changed -= OnWysiwygChanged;
            _wysiwyg.Changed += OnWysiwygChanged;

            await _wysiwyg.LoadAsync(GetEditorPlainText());
            ApplyZoomToWebViews();
            _wysiwygOn = true;

            Editor.Visibility = Visibility.Collapsed;
            PreviewSplitter.Visibility = Visibility.Collapsed;
            Preview.Visibility = Visibility.Collapsed;
            WysiwygEditor.Visibility = Visibility.Visible;
            _viewModel.IsPreviewMode = false;
            _wysiwyg.Focus();
        }
        catch (Exception ex)
        {
            _wysiwygOn = false;
            WysiwygEditor.Visibility = Visibility.Collapsed;
            _viewModel.IsPreviewMode = true;
            UpdateContentView();
            MessageBox.Show(this, string.Format(Strings.Note_WysiwygUnavailable, ex.Message), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _wysiwygEntering = false; }
    }
    private async Task ExitWysiwygAsync(bool save)
    {
        if (!_wysiwygOn) return;
        try
        {
            if (save && _wysiwyg is not null)
            {
                var md = await _wysiwyg.GetMarkdownAsync();
                _suppressEditorSync = true;
                try { _viewModel.UpdateContent(md, NoteContentFormat.Markdown); LoadEditorContent(); }
                finally { _suppressEditorSync = false; }
            }
        }
        catch { /* UI returns even if save fails */ }
        finally
        {
            _wysiwygOn = false;
            WysiwygEditor.Visibility = Visibility.Collapsed;
            _viewModel.IsPreviewMode = true;
            UpdateContentView();
        }
    }

    private void OnWysiwygChanged(string md)
    {
        if (!_wysiwygOn) return;
        _viewModel.UpdateContent(md, NoteContentFormat.Markdown);   // Save as markdown source
    }

    /// Whether WYSIWYG editing is turned on — The basis for Vault Watch to treat it as "has unsaved edits."
    internal bool IsWysiwygActive => _wysiwygOn;

    /// When a hub (to-do toggle, etc.) changes the note contents within the app, it is reflected in the open window.
    /// Unlike hydrate(<see cref="ApplyExternalContentAsync"/>), it burns vm.UpdateContent.
    /// Debounce storage + synchronization of linked files also occurs — because the source of the content is not a file but an app.
    internal async Task ApplyLocalEditAsync(string content)
    {
        if (_wysiwygOn && _wysiwyg is not null)
        {
            await _wysiwyg.SetMarkdownAsync(content).ConfigureAwait(true);   // suppress on JS side — no echo
        }
        else
        {
            ReplaceEditorText(content);                                      // Block re-entry with _suppressEditorSync
        }
        _viewModel.UpdateContent(content, _viewModel.Format);                // Schedule → Save debounce
        if (!_wysiwygOn && IsMarkup && Preview.Visibility == Visibility.Visible) RenderPreview();
    }

    /// Apply external changes reflected by vault watch: VM hydrate (no save schedule) + editor/WYSIWYG replacement +
    /// Preview re-render. It is a path of the same nature as reflecting external changes to linked files (ReloadLinkedFileAsync).
    internal async Task ApplyExternalContentAsync(string content, NoteContentFormat format)
    {
        _viewModel.HydrateExternal(content, format);

        if (_wysiwygOn && _wysiwyg is not null && format == NoteContentFormat.Markdown)
        {
            await _wysiwyg.SetMarkdownAsync(content).ConfigureAwait(true);
        }
        else
        {
            if (_wysiwygOn) await ExitWysiwygAsync(save: false).ConfigureAwait(true);  // In rare cases where the format has changed
            ReplaceEditorText(content);
        }

        UpdateToolbarVisibility();
        if (!_wysiwygOn) UpdateContentView();
    }



    private async void OnWindowDeactivated(object? sender, EventArgs e)
    {
        UpdateToolbarVisibility();
        if (_wysiwygOn) await ExitWysiwygAsync(save: true);
    }

    private async void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_wysiwygOn || WysiwygEditor.IsMouseOver) return;
        await ExitWysiwygAsync(save: true);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    private void FullscreenButton_OnClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen) return;
        _fullscreenRestoreBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        _isFullscreen = true;
        _suppressGeometrySync = true;
        try
        {
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        finally { _suppressGeometrySync = false; }

        FullscreenIcon.Data = Geometry.Parse("M6,2 V6 H2 M14,6 H10 V2 M10,14 V10 H14 M2,10 H6 V14");
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen) return;
        _isFullscreen = false;
        _suppressGeometrySync = true;
        try
        {
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Left = _fullscreenRestoreBounds.Left;
            Top = _fullscreenRestoreBounds.Top;
            Width = _fullscreenRestoreBounds.Width;
            Height = _fullscreenRestoreBounds.Height;
        }
        finally { _suppressGeometrySync = false; }

        FullscreenIcon.Data = Geometry.Parse("M2,6 V2 H6 M10,2 H14 V6 M14,10 V14 H10 M6,14 H2 V10");
    }

    private void OnWebViewZoomChanged(double factor)
    {
        if (_syncingZoom) return;
        var clamped = Math.Clamp(factor, 0.25, 5.0);
        if (Math.Abs(_viewModel.ZoomFactor - clamped) > 0.001)
            _viewModel.ZoomFactor = clamped;
    }

    private void ApplyZoomToWebViews()
    {
        _syncingZoom = true;
        try
        {
            Preview.ZoomFactor = _viewModel.ZoomFactor;
            try { WysiwygEditor.ZoomFactor = _viewModel.ZoomFactor; } catch { /* editor WebView may not be initialized yet */ }
        }
        finally { _syncingZoom = false; }
    }

    private void UpdateContentView()
    {
        // Exactly two content states: read-only Eye preview and WYSIWYG editing.
        Editor.Visibility = Visibility.Collapsed;
        PreviewSplitter.Visibility = Visibility.Collapsed;
        EditorRow.Height = new GridLength(0);
        SplitterRow.Height = new GridLength(0);
        PreviewRow.Height = new GridLength(1, GridUnitType.Star);

        if (_wysiwygOn)
        {
            Preview.Visibility = Visibility.Collapsed;
            WysiwygEditor.Visibility = Visibility.Visible;
            return;
        }

        WysiwygEditor.Visibility = Visibility.Collapsed;
        Preview.Visibility = Visibility.Visible;
        RenderPreview();
    }

    private async void RenderPreview()
    {
        try
        {
            var html = HtmlRenderer.Render(GetEditorPlainText(), _viewModel.Format, _viewModel.Theme);
            await EnsureWebViewAsync().ConfigureAwait(true);
            ApplyZoomToWebViews();
            Preview.NavigateToString(html);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                string.Format(Strings.Note_PreviewError, ex.Message),
                "Stickmark", MessageBoxButton.OK, MessageBoxImage.Warning);
            _viewModel.IsPreviewMode = true;
        }
    }

    private bool _webViewReady;

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;

        var udf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stickmark", "WebView2");
        Directory.CreateDirectory(udf);
        Preview.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = udf };
        await Preview.EnsureCoreWebView2Async().ConfigureAwait(true);

        var core = Preview.CoreWebView2;
        // Page-level CSP keeps note markup script-free. The host still injects one tiny, trusted listener
        // via ExecuteScriptAsync so a double-click can request edit mode.
        core.Settings.IsScriptEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // Real links open in the user's browser instead of navigating inside the note.
        core.NavigationStarting += (_, e) =>
        {
            if (e.IsUserInitiated && (e.Uri.StartsWith("http://") || e.Uri.StartsWith("https://")))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.WebMessageReceived += OnPreviewWebMessage;
        core.NavigationCompleted += async (_, _) => await InstallPreviewDoubleClickHookAsync();

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            // Restrict the scheme to http/https — Note that the markup can be executed locally with the file:/UNC/protocol handler.
            // Prevent navigation (same policy as NavigationStarting).
            if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                OpenExternal(e.Uri);
            }
        };
        _webViewReady = true;
    }


    private async Task InstallPreviewDoubleClickHookAsync()
    {
        if (Preview.CoreWebView2 is null) return;
        const string script = """
            (() => {
              if (window.__stickmarkPreviewHooked) return;
              window.__stickmarkPreviewHooked = true;
              document.addEventListener('dblclick', (event) => {
                if (event.button !== 0) return;
                window.chrome.webview.postMessage('stickmark:edit');
              }, true);
            })();
            """;
        try { await Preview.CoreWebView2.ExecuteScriptAsync(script); } catch { /* preview may have navigated away */ }
    }

    private void OnPreviewWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }

        if (!string.Equals(message, "stickmark:edit", StringComparison.Ordinal)) return;
        if (_viewModel.Format != NoteContentFormat.Markdown || _wysiwygOn) return;
        _ = Dispatcher.InvokeAsync(async () => await EnterWysiwygAsync());
    }

    private static void OpenExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    // ── Open file (dialog box / drag & drop) ──────────────────────────────────────

    private async void OpenFileViaDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = LinkedFile.OpenDialogFilter,
            Title = Strings.Tray_OpenMarkdownDialogTitle,
        };
        if (dlg.ShowDialog(this) == true)
        {
            try { await _onOpenFileRequested(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, string.Format(Strings.Note_OpenFileError, ex.Message), "Stickmark",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OpenFileButton_OnClick(object sender, RoutedEventArgs e) => OpenFileViaDialog();

    private void Window_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void Window_OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        foreach (var file in files)
        {
            if (!LinkedFile.IsSupported(file)) continue;
            try { await _onOpenFileRequested(file); }
            catch (Exception ex)
            {
                MessageBox.Show(this, string.Format(Strings.Note_OpenFileError, ex.Message), "Stickmark",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    // ── Monitoring external changes to linked files ────────────────────────────────────────────────

    private void StartFileWatcherIfLinked()
    {
        if (!_viewModel.IsLinkedFile) return;
        var path = _viewModel.LinkedFilePath!;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;

            _fileWatcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _fileWatcher.Changed += OnLinkedFileEvent;
            _fileWatcher.Created += OnLinkedFileEvent;
            _fileWatcher.Renamed += OnLinkedFileEvent;
            _fileWatcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // Monitoring is an additional function — even if it fails, editing/saving works normally.
            DisposeFileWatcher();
        }
    }

    private void OnLinkedFileEvent(object sender, FileSystemEventArgs e)
    {
        // Callback is passed to the threadpool thread — UI to restart the debounce timer.
        Dispatcher.BeginInvoke(() => { _fileReloadTimer.Stop(); _fileReloadTimer.Start(); });
    }

    private async Task ReloadLinkedFileAsync()
    {
        if (!_viewModel.IsLinkedFile || _reloadPromptOpen) return;
        var path = _viewModel.LinkedFilePath!;

        string fileText;
        try { (fileText, _) = await LinkedFile.ReadAsync(path).ConfigureAwait(true); }
        catch { return; } // File is temporarily locked or being deleted — retry on next event

        var editorText = GetEditorPlainText();
        if (string.Equals(fileText, editorText, StringComparison.Ordinal))
        {
            // This is what we just wrote, or no real changes — just update the sync baseline.
            _viewModel.MarkSynced(fileText);
            return;
        }

        var hasLocalEdits = !string.Equals(editorText, _viewModel.LastSyncedText, StringComparison.Ordinal);
        if (hasLocalEdits)
        {
            _reloadPromptOpen = true;
            try
            {
                var choice = MessageBox.Show(this,
                    Strings.Note_FileChangedPrompt,
                    Strings.Note_FileChangedTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (choice != MessageBoxResult.Yes) return;
            }
            finally { _reloadPromptOpen = false; }
        }

        ReplaceEditorText(fileText);
        _viewModel.UpdateContent(fileText, _viewModel.Format);
        _viewModel.MarkSynced(fileText);
        if (IsMarkup && Preview.Visibility == Visibility.Visible) RenderPreview();
    }

    private void ReplaceEditorText(string text)
    {
        _suppressEditorSync = true;
        try
        {
            Editor.Document.Blocks.Clear();
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                Editor.Document.Blocks.Add(new Paragraph(new Run(line)));
            }
        }
        finally
        {
            _suppressEditorSync = false;
        }
    }

    private void DisposeFileWatcher()
    {
        _fileReloadTimer?.Stop();
        if (_fileWatcher is null) return;
        try
        {
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Changed -= OnLinkedFileEvent;
            _fileWatcher.Created -= OnLinkedFileEvent;
            _fileWatcher.Renamed -= OnLinkedFileEvent;
            _fileWatcher.Dispose();
        }
        catch { /* nothing to do */ }
        _fileWatcher = null;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isFullscreen) ExitFullscreen();
        try { await _viewModel.FlushAsync().ConfigureAwait(true); }
        catch { /* logged in VM */ }

        if (_allowClose || _viewModel.IsEmpty)
        {
            // Empty notes really close (and the manager will delete them); explicit deletes also flow through.
            _onHidden(this);
            return;
        }

        // Spec: closing the X with content keeps it alive via the tray — we hide instead of dispose.
        e.Cancel = true;
        Hide();
        _onHidden(this);
    }

    private void HeaderBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void NewButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = _onNewRequested();
        window.Show();
        window.Activate();
    }

    private void ListButton_OnClick(object sender, RoutedEventArgs e) => _onListRequested();

    private async void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        var message = _viewModel.IsLinkedFile
            ? string.Format(Strings.Note_DeleteLinkedConfirm, Path.GetFileName(_viewModel.LinkedFilePath!))
            : Strings.Note_DeleteConfirm;
        var confirm = MessageBox.Show(
            this,
            message,
            "Stickmark",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;
        await _viewModel.DeleteCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    private void PopulateTemplateMenu()
    {
        foreach (var template in NoteTemplates.All)
        {
            var item = new MenuItem { Header = template.Name() };
            item.Click += (_, _) => App.Services.GetRequiredService<IWindowManager>().CreateAndShowNew(template);
            TemplateMenu.Items.Add(item);
        }
    }

    private void MoreMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void ExportNotePdf_OnClick(object sender, RoutedEventArgs e) => await ExportCurrentNoteAsync(ExportFormat.Pdf);
    private async void ExportNoteHtml_OnClick(object sender, RoutedEventArgs e) => await ExportCurrentNoteAsync(ExportFormat.Html);
    private async void ExportNoteMarkdown_OnClick(object sender, RoutedEventArgs e) => await ExportCurrentNoteAsync(ExportFormat.MarkdownFiles);

    private async Task ExportCurrentNoteAsync(ExportFormat format)
    {
        try
        {
            await _viewModel.FlushAsync().ConfigureAwait(true); // Reflect the latest content
            await App.Services.GetRequiredService<IBackupService>().ExportSingleNoteAsync(_viewModel.Note, format).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format(Strings.Note_ExportError, ex.Message), "Stickmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PrintNote_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.FlushAsync().ConfigureAwait(true);
            await App.Services.GetRequiredService<IBackupService>().PrintNoteAsync(_viewModel.Note).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format(Strings.Note_PrintError, ex.Message), "Stickmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ColorSwatch_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NoteColor color)
        {
            _viewModel.SetColorCommand.Execute(color);
            ColorPickerToggle.IsChecked = false;
        }
    }

    private void BoldToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        EditingCommands.ToggleBold.Execute(null, Editor);
        Editor.Focus();
    }

    private void ItalicToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        EditingCommands.ToggleItalic.Execute(null, Editor);
        Editor.Focus();
    }

    private void UnderlineToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        EditingCommands.ToggleUnderline.Execute(null, Editor);
        Editor.Focus();
    }

    private void StrikeToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        ToggleStrikethrough();
    }

    private void ToggleStrikethrough()
    {
        if (Editor.Selection.IsEmpty) return;
        var current = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty)
            as TextDecorationCollection;
        var hasStrike = current?.Any(d => d.Location == TextDecorations.Strikethrough.First().Location) == true;
        Editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty,
            hasStrike ? null : TextDecorations.Strikethrough);
        Editor.Focus();
    }

    private void AlignLeftToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        EditingCommands.AlignLeft.Execute(null, Editor);
        Editor.Focus();
    }

    private void AlignCenterToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        EditingCommands.AlignCenter.Execute(null, Editor);
        Editor.Focus();
    }

    private void AlignRightToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        EditingCommands.AlignRight.Execute(null, Editor);
        Editor.Focus();
    }

    private void BulletsToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        EditingCommands.ToggleBullets.Execute(null, Editor);
        Editor.Focus();
    }

    private void NumberingToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        EditingCommands.ToggleNumbering.Execute(null, Editor);
        Editor.Focus();
    }

    private void TaskItem_OnClick(object sender, RoutedEventArgs e)
    {
        InsertTaskAtCaret();
        Editor.Focus();
    }

    private void InlineCode_OnClick(object sender, RoutedEventArgs e) => ToggleInlineCode();

    private void ToggleInlineCode()
    {
        if (Editor.Selection.IsEmpty) return;
        var monospace = (FontFamily)new FontFamilyConverter().ConvertFromString("Cascadia Mono, Consolas, monospace")!;
        var current = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily;
        var isCode = current is not null && string.Equals(current.Source, monospace.Source, StringComparison.OrdinalIgnoreCase);
        if (isCode)
        {
            Editor.Selection.ClearAllProperties();
        }
        else
        {
            Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, monospace);
            var bg = new SolidColorBrush(CodeBackground);
            bg.Freeze();
            Editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, bg);
        }
        Editor.Focus();
    }

    private void CodeBlock_OnClick(object sender, RoutedEventArgs e)
    {
        var paragraph = Editor.Selection.Start.Paragraph ?? Editor.CaretPosition.Paragraph;
        if (paragraph is null) return;

        var monospace = (FontFamily)new FontFamilyConverter().ConvertFromString("Cascadia Mono, Consolas, monospace")!;
        paragraph.FontFamily = monospace;
        var bg = new SolidColorBrush(CodeBlockBackground);
        bg.Freeze();
        paragraph.Background = bg;
        paragraph.Padding = new Thickness(8, 6, 8, 6);
        paragraph.Margin = new Thickness(0, 4, 0, 8);
        Editor.Focus();
    }

    private void ClearFormatting_OnClick(object sender, RoutedEventArgs e)
    {
        if (Editor.Selection.IsEmpty) Editor.SelectAll();
        Editor.Selection.ClearAllProperties();
        Editor.Focus();
        SyncToolbarFromSelection();
    }

    private void FontSizeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToolbarSync) return;
        if (FontSizeCombo.SelectedItem is double size && Editor.Selection is not null)
        {
            if (Editor.Selection.IsEmpty)
            {
                _viewModel.EditorFontSize = size;
            }
            else
            {
                Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
            }
            Editor.Focus();
        }
    }

    private void InsertTaskAtCaret()
    {
        var checkbox = new CheckBox
        {
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        checkbox.Checked += (_, _) => _viewModel.UpdateContent(SerializeDocument(), NoteContentFormat.RichTextXaml);
        checkbox.Unchecked += (_, _) => _viewModel.UpdateContent(SerializeDocument(), NoteContentFormat.RichTextXaml);

        var inline = new InlineUIContainer(checkbox, Editor.CaretPosition);
        Editor.CaretPosition = inline.ElementEnd;
    }

    private string SerializeDocument()
    {
        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Xaml);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void DecorateLinks()
    {
        // Wrap raw `[[Title]]` text in plain Runs into Hyperlinks. Skips runs already inside a Hyperlink so we
        // don't keep nesting on every keystroke. Suppresses re-entrant TextChanged while the tree is rewritten.
        var snapshot = CollectRuns(Editor.Document).ToList();
        if (snapshot.Count == 0) return;

        var wasSuppressed = _suppressEditorSync;
        _suppressEditorSync = true;
        try
        {
            foreach (var run in snapshot)
            {
                DecorateRun(run);
            }
        }
        finally
        {
            _suppressEditorSync = wasSuppressed;
        }
    }

    private void DecorateRun(Run run)
    {
        var text = run.Text;

        // Collect both `[[wiki]]` links and bare http(s) URLs, then wrap each once.
        var decorations = new List<(int Index, int Length, Uri Uri, string Tooltip, string Expected)>();
        foreach (var (index, length, title) in TextExtraction.FindLinks(text))
        {
            decorations.Add((index, length,
                new Uri($"stickmark://note/{Uri.EscapeDataString(title)}", UriKind.Absolute),
                $"Open '{title}'", $"[[{title}]]"));
        }
        foreach (var (index, length, url) in TextExtraction.FindUrls(text))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                decorations.Add((index, length, uri, url, url));
            }
        }
        if (decorations.Count == 0) return;

        // Replace from the back to keep earlier offsets stable.
        foreach (var (index, length, uri, tooltip, expected) in decorations.OrderByDescending(d => d.Index))
        {
            try
            {
                var startPtr = run.ContentStart.GetPositionAtOffset(index);
                var endPtr = run.ContentStart.GetPositionAtOffset(index + length);
                if (startPtr is null || endPtr is null) continue;

                var range = new TextRange(startPtr, endPtr);
                if (range.Text != expected) continue;

                var hyperlink = new Hyperlink(range.Start, range.End)
                {
                    NavigateUri = uri,
                    ToolTip = tooltip,
                };
                hyperlink.RequestNavigate += OnLinkRequestNavigate;
            }
            catch
            {
                // Pointer math can fail across complex inline structures — skip.
            }
        }
    }

    private void OnLinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        if (e.Uri.Scheme is "http" or "https")
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, string.Format(Strings.Note_OpenLinkError, ex.Message), "Stickmark",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        // Otherwise it's a stickmark://note/{title} wiki link.
        var title = Uri.UnescapeDataString(e.Uri.AbsolutePath.TrimStart('/'));
        if (!_onLinkActivated(title))
        {
            MessageBox.Show(this, string.Format(Strings.Note_NoteNotFound, title), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static IEnumerable<Run> CollectRuns(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
        {
            foreach (var run in CollectRuns(block))
            {
                yield return run;
            }
        }
    }

    private static IEnumerable<Run> CollectRuns(Block block)
    {
        switch (block)
        {
            case Paragraph p:
                foreach (var run in CollectRuns(p.Inlines)) yield return run;
                break;
            case Section s:
                foreach (var inner in s.Blocks)
                    foreach (var run in CollectRuns(inner)) yield return run;
                break;
            case List list:
                foreach (var item in list.ListItems)
                    foreach (var inner in item.Blocks)
                        foreach (var run in CollectRuns(inner)) yield return run;
                break;
            case BlockUIContainer:
                yield break;
        }
    }

    private static IEnumerable<Run> CollectRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Hyperlink:
                    // Already a link — skip to avoid nesting.
                    break;
                case Run r:
                    yield return r;
                    break;
                case Span span:
                    foreach (var inner in CollectRuns(span.Inlines)) yield return inner;
                    break;
            }
        }
    }

    private sealed class RelayCommandImpl : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommandImpl(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
