using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Stickmark.Utils;

/// Render the HTML document in off-screen WebView2 and save it as a PDF file.
/// WPF's WebView2 control requires an HWND to be initialized, so place it in a hidden window off the screen.
/// Close immediately after rendering. Must be called from the UI (STA) thread.
public static class PdfRenderer
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);

    public static async Task RenderAsync(string html, string pdfPath)
    {
        // NavigateToString has a limit of about 2MB (compared to image embedding), so it is bypassed as a temporary file.
        var tempHtml = Path.Combine(Path.GetTempPath(), $"stickmark-export-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(tempHtml, html).ConfigureAwait(true);

        // Dedicated folder separate from note preview (WebView2) — The one-time printing environment is different from the constant preview environment.
        // Avoid sharing locks/cache.
        var udf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stickmark", "WebView2-export");
        Directory.CreateDirectory(udf);

        // It is robust to set CreationProperties before adding them to the tree.
        var web = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = udf } };
        var host = new Window
        {
            Width = 900,
            Height = 1200,
            // Hide it off the screen. Opacity=0 makes the window a layered (transparent) window, allowing WebView2 hosting.
            // Do not use because it is rejected (ERROR_INVALID_STATE) — it is hidden from the user's view just by its location.
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            Content = web,
        };

        try
        {
            host.Show();   // Obtain HWND — Prerequisite for WebView2 initialization.
            await web.EnsureCoreWebView2Async().ConfigureAwait(true);

            // Completely blocks script/host communication, same as note render.
            var settings = web.CoreWebView2.Settings;
            settings.IsScriptEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;

            var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNav(object? _, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult(e.IsSuccess);
            // If the renderer process dies, NavigationCompleted may never come, so wake it up with an exception.
            void OnProcFailed(object? _, CoreWebView2ProcessFailedEventArgs e) =>
                navigated.TrySetException(new InvalidOperationException(
                    "The WebView2 process terminated unexpectedly: " + e.ProcessFailedKind));

            web.NavigationCompleted += OnNav;
            web.CoreWebView2.ProcessFailed += OnProcFailed;
            try
            {
                web.CoreWebView2.Navigate(new Uri(tempHtml).AbsoluteUri);
                // Handle a timeout if the local file render does not finish in time (to avoid freezing).
                if (await Task.WhenAny(navigated.Task, Task.Delay(NavigationTimeout)).ConfigureAwait(true) != navigated.Task)
                {
                    throw new TimeoutException("Document rendering did not finish within the time limit.");
                }
                var success = await navigated.Task.ConfigureAwait(true);   // Propagate exception (ProcessFailed).
                if (!success) throw new InvalidOperationException("Failed to render document for export.");
            }
            finally
            {
                web.NavigationCompleted -= OnNav;
                web.CoreWebView2.ProcessFailed -= OnProcFailed;
            }

            var print = web.CoreWebView2.Environment.CreatePrintSettings();
            print.ShouldPrintBackgrounds = true;
            var ok = await web.CoreWebView2.PrintToPdfAsync(pdfPath, print).ConfigureAwait(true);
            if (!ok) throw new InvalidOperationException("WebView2 PrintToPdf returned failure.");
        }
        finally
        {
            web.Dispose();
            host.Close();
            try { File. Delete(tempHtml); } catch { /* Ignore temporary file cleanup failure */ }
        }
    }
}
