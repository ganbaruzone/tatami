using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Tatami.Services;

public sealed record NoteTemplate(string Id, string Name, string Contents);

public sealed class NoteTemplateService
{
    public IReadOnlyList<NoteTemplate> Templates { get; } =
    [
        new("blank", "空のノート", "---\ntitle: {{title_yaml}}\ndate: {{date}}\npublish: false\n---\n\n"),
        new("article", "記事", "---\ntitle: {{title_yaml}}\ndate: {{date}}\ntags: []\npublish: false\n---\n\n# {{title}}\n\n"),
        new("meeting", "議事録", "---\ntitle: {{title_yaml}}\ndate: {{date}}\ntags: [meeting]\npublish: false\n---\n\n# {{title}}\n\n## 参加者\n\n## 議題\n\n## 決定事項\n\n## 次のアクション\n\n"),
        new("daily", "日報", "---\ntitle: {{title_yaml}}\ndate: {{date}}\ntags: [daily]\npublish: false\n---\n\n# {{title}}\n\n## 今日の予定\n\n## 作業記録\n\n## 振り返り\n\n")
    ];

    public string Render(string templateId, string title, DateOnly date)
    {
        var template = Templates.FirstOrDefault(item => item.Id == templateId)
            ?? throw new ArgumentException("指定されたテンプレートがありません。", nameof(templateId));
        return template.Contents
            .Replace("{{title_yaml}}", JsonSerializer.Serialize(title), StringComparison.Ordinal)
            .Replace("{{title}}", title, StringComparison.Ordinal)
            .Replace("{{date}}", date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
