using System;
using System.Collections.Generic;
using Stickmark.Resources;

namespace Stickmark.Models;

/// Built-in note template. Name is lazy evaluated (resource) in the current language, and "{{date}}" in Content is replaced with today's date when created.
public sealed record NoteTemplate(Func<string> Name, string Content, NoteContentFormat Format, NoteColor Color);

public static class NoteTemplates
{
    public static IReadOnlyList<NoteTemplate> All { get; } = new List<NoteTemplate>
    {
        new(() => Strings.Template_DailyLog,
            "# {{date}}\n\n## To-do\n- [ ] \n\n## Notes\n", NoteContentFormat.Markdown, NoteColor.Blue),
        new(() => Strings.Template_Meeting,
            "# Meeting — {{date}}\n\nAttendees: \n\n## Agenda\n- \n\n## Decisions\n- \n\n## Action items\n- [ ] ",
            NoteContentFormat.Markdown, NoteColor.Green),
        new(() => Strings.Template_Todo,
            "# To-do\n\n- [ ] \n- [ ] \n- [ ] ", NoteContentFormat.Markdown, NoteColor.Yellow),
    };
}
