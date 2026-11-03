using System;
using System.Collections.Generic;
using System.Text;
using ColorCode;
using ColorCode.Styling;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MdHtmlRenderer = Markdig.Renderers.HtmlRenderer;

namespace Stickmark.Utils;

/// Render the fence code block (```lang) by highlighting it with ColorCode (inline style).
/// In case of unsupported language, no information, defense block, and highlighter failure, fallback to Markdig's default renderer (same as existing output).
/// No script is added — only inline style attributes are emitted, so the preview/export CSP remains restrictive.
/// The invariants remain the same (docs/SECURITY-REVIEW.md).
internal sealed class CodeBlockHighlightRenderer : HtmlObjectRenderer<CodeBlock>
{
    private static readonly CodeBlockRenderer Fallback = new();

    /// Markdown information string (```js, etc.) → ColorCode language id. Fallback for languages ​​that do not exist (yaml/bash, etc.).
    private static readonly Dictionary<string, string> AliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["js"] = "javascript", ["jsx"] = "javascript", ["node"] = "javascript", ["javascript"] = "javascript",
        ["json"] = "javascript", // There is no JSON in ColorCode, so it is approximated with JS grammar (escape path is the same)
        ["ts"] = "typescript", ["tsx"] = "typescript", ["typescript"] = "typescript",
        ["py"] = "python", ["python"] = "python",
        ["cs"] = "csharp", ["c#"] = "csharp", ["csharp"] = "csharp",
        ["c"] = "cpp", ["cpp"] = "cpp", ["c++"] = "cpp", ["cc"] = "cpp", ["h"] = "cpp", ["hpp"] = "cpp",
        ["html"] = "html", ["htm"] = "html",
        ["css"] = "css",
        ["xml"] = "xml", ["xaml"] = "xml", ["csproj"] = "xml",
        ["sql"] = "sql",
        ["java"] = "java",
        ["ps1"] = "powershell", ["ps"] = "powershell", ["powershell"] = "powershell",
    };

    protected override void Write(MdHtmlRenderer renderer, CodeBlock block)
    {
        var language = FindLanguage(block);
        if (language is null)
        {
            ((IMarkdownObjectRenderer)Fallback).Write(renderer, block);   // Same escape output as before
            return;
        }

        string html;
        try
        {
            // ColorCode HTML escapes the code text and only applies inline styles to the span.
            html = new HtmlFormatter(StyleDictionary.DefaultLight).GetHtmlString(ExtractSource(block), language);
        }
        catch (Exception)
        {
            ((IMarkdownObjectRenderer)Fallback).Write(renderer, block);   // Safe fallback if highlighter fails
            return;
        }

        renderer.EnsureLine();
        renderer.WriteLine(html);
    }

    private static ILanguage? FindLanguage(CodeBlock block)
    {
        if (block is not FencedCodeBlock { Info.Length: > 0 } fenced) return null;
        // Treat only the first token of the information string as language (ignoring expansions such as "csharp title=x").
        var token = fenced.Info.Trim();
        var space = token.IndexOf(' ');
        if (space > 0) token = token[..space];
        return AliasMap.TryGetValue(token, out var id) ? Languages.FindById(id) : null;
    }

    private static string ExtractSource(CodeBlock block)
    {
        var sb = new StringBuilder();
        var lines = block.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines.Lines[i].Slice.ToString());
        }
        return sb.ToString();
    }
}
