using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tatami.Services;

public static class NoteMetadataEditor
{
    private static readonly Regex SettingLine = new("^[\\\"']?(title|publish|slug)[\\\"']?\\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Apply(string text, string? title, bool publish, string? slug)
    {
        title = string.IsNullOrWhiteSpace(title) ? null : title;
        slug = string.IsNullOrWhiteSpace(slug) ? null : PathSafety.Slug(slug);
        _ = new NoteRepository().Parse("note.md", text);

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hasBom = text.StartsWith('\uFEFF');
        var content = hasBom ? text[1..] : text;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count == 0 || lines[0].Trim() != "---")
            return (hasBom ? "\uFEFF" : "") + CreateFrontMatter(title, publish, slug, newline) + content;

        var end = lines.FindIndex(1, line => line.Trim() is "---" or "...");
        if (end < 0) throw new InvalidDataException("Front Matterの終端がありません。");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = new List<string>(lines.Count + 3) { lines[0] };
        for (var i = 1; i < end; i++)
        {
            var match = SettingLine.Match(lines[i]);
            if (!match.Success) { updated.Add(lines[i]); continue; }
            var key = match.Groups[1].Value.ToLowerInvariant();
            if (!seen.Add(key)) throw new InvalidDataException($"Front Matterに {key} が複数あります。");
            var value = key switch
            {
                "title" => title is null ? null : $"title: {JsonSerializer.Serialize(title)}",
                "publish" => $"publish: {publish.ToString().ToLowerInvariant()}",
                "slug" => slug is null ? null : $"slug: {JsonSerializer.Serialize(slug)}",
                _ => null
            };
            if (value is not null) updated.Add(value);
        }

        if (!seen.Contains("title") && title is not null) updated.Add($"title: {JsonSerializer.Serialize(title)}");
        if (!seen.Contains("publish")) updated.Add($"publish: {publish.ToString().ToLowerInvariant()}");
        if (!seen.Contains("slug") && slug is not null) updated.Add($"slug: {JsonSerializer.Serialize(slug)}");
        updated.AddRange(lines.Skip(end));
        var result = string.Join('\n', updated);
        return (hasBom ? "\uFEFF" : "") + (newline == "\n" ? result : result.Replace("\n", newline, StringComparison.Ordinal));
    }

    private static string CreateFrontMatter(string? title, bool publish, string? slug, string newline)
    {
        var rows = new List<string> { "---" };
        if (title is not null) rows.Add($"title: {JsonSerializer.Serialize(title)}");
        rows.Add($"publish: {publish.ToString().ToLowerInvariant()}");
        if (slug is not null) rows.Add($"slug: {JsonSerializer.Serialize(slug)}");
        rows.Add("---");
        rows.Add("");
        return string.Join(newline, rows) + newline;
    }
}
