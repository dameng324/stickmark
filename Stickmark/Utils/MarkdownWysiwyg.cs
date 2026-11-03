using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Stickmark.Utils;

/// WebView2 bridge wrapped around an offline CodeMirror 6-based WYSIWYG markdown editor.
///
/// Security: the render-only preview (<c>Preview</c>) keeps a strict CSP and only runs a host-injected double-click listener; notes cannot execute scripts.
/// Scripts/web messages are allowed only in WebView. Instead, (1) completely block the network (virtual host's
/// Local assets only, CSP is <c>default-src 'none'</c> in page <c>editor.html</c>),
/// (2) Note content is not injected into HTML, but only <b>string data</b> is exchanged (setMarkdown/getMarkdown)
/// Does not create an execution path for old XAML gadget-style code.
public sealed class MarkdownWysiwyg
{
    private const string VirtualHost = "stickmark.editor";

    private readonly WebView2 _web;
    private TaskCompletionSource<bool>? _ready;
    private bool _initialized;

    /// Called when the edit content (debounced Markdown source) changes.
    public event Action<string>? Changed;

    /// Raised when the user presses Escape inside the editor and wants to return to preview.
    public event Action? EscapeRequested;

    public MarkdownWysiwyg(WebView2 web) => _web = web;

    public async Task InitAsync()
    {
        if (_initialized) return;

        var udf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stickmark", "WebView2");
        Directory.CreateDirectory(udf);
        _web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = udf };
        await _web.EnsureCoreWebView2Async().ConfigureAwait(true);

        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.IsScriptEnabled = true;             // Required to run CM6 (this edit WebView only)
        s.IsWebMessageEnabled = true;         // content synchronization channel
        s.AreHostObjectsAllowed = false;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;

        // Extract embedded editor assets to a real folder and map them to a virtual host —
        // Only load local assets, block cross-origin. It also works with single file distribution.
        var assets = EnsureAssetsExtracted();
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, assets, CoreWebView2HostResourceAccessKind.DenyCors);

        core.WebMessageReceived += OnWebMessage;
        // Block all movement and new windows outside the editor page.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;

        _initialized = true;
    }

    /// Loads the editor page and injects markdown when ready.
    public async Task LoadAsync(string markdown)
    {
        await InitAsync().ConfigureAwait(true);
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Pass the app UI language to the editor — localize toolbar tooltips and placeholders.
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        _web.CoreWebView2.Navigate($"https://{VirtualHost}/editor.html?lang={lang}");
        await _ready.Task.ConfigureAwait(true);       // Wait for 'ready' message
        await SetMarkdownAsync(markdown).ConfigureAwait(true);
    }

    public async Task SetMarkdownAsync(string markdown)
    {
        var json = JsonSerializer.Serialize(markdown ?? string.Empty);
        await _web.CoreWebView2.ExecuteScriptAsync($"window.SPEditor.setMarkdown({json})")
            .ConfigureAwait(true);
    }

    public async Task<string> GetMarkdownAsync()
    {
        var raw = await _web.CoreWebView2.ExecuteScriptAsync("window.SPEditor.getMarkdown()")
            .ConfigureAwait(true);
        // ExecuteScriptAsync returns JSON encoded results.
        try { return JsonSerializer.Deserialize<string>(raw) ?? string.Empty; }
        catch { return string.Empty; }
    }

    public void Focus() =>
        _ = _web.CoreWebView2?.ExecuteScriptAsync("window.SPEditor && window.SPEditor.focus()");

    private static readonly object ExtractLock = new();
    private static string? _assetsDir;

    /// Extracts the editor assets embedded in the assembly to a local folder and returns the path.
    /// Extract only once per process (to avoid contention when creating concurrent windows).
    private static string EnsureAssetsExtracted()
    {
        lock (ExtractLock)
        {
            if (_assetsDir is not null) return _assetsDir;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Stickmark", "mdeditor");
            Directory.CreateDirectory(dir);

            var asm = typeof(MarkdownWysiwyg).Assembly;
            ExtractResource(asm, "editor.html", Path.Combine(dir, "editor.html"));
            ExtractResource(asm, "mdeditor.bundle.js", Path.Combine(dir, "mdeditor.bundle.js"));

            _assetsDir = dir;
            return dir;
        }
    }

    private static void ExtractResource(Assembly asm, string endsWith, string destPath)
    {
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + endsWith, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            throw new FileNotFoundException($"Embedded editor asset not found: {endsWith}");
        using var src = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Cannot open embedded asset: {name}");
        using var fs = File.Create(destPath);
        src.CopyTo(fs);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string body;
        try { body = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "ready") { _ready?.TrySetResult(true); return; }
            if (type == "change" && doc.RootElement.TryGetProperty("md", out var md))
                Changed?.Invoke(md.GetString() ?? string.Empty);
            else if (type == "escape")
                EscapeRequested?.Invoke();
        }
        catch { /* Ignore message of unknown format */ }
    }
}
