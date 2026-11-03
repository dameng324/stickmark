namespace Stickmark.Services;

/// In what format will the selected notes be exported.
public enum ExportFormat
{
    /// 1 .md file per note, saved in the selected folder.
    MarkdownFiles,

    /// Save all notes as a single styled .html document.
    Html,

    /// Render the above HTML document with WebView2 and save it as a single .pdf.
    Pdf,
}
