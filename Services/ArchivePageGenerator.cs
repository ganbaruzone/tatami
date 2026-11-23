using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Tatami.Models;

namespace Tatami.Services;

internal static class ArchivePageGenerator
{
    public static void Add(IReadOnlyList<Note> notes, IReadOnlyDictionary<string, string> routes,
        SiteArchiveOptions options, Dictionary<string, byte[]> generated, string output)
    {
        if (options.SortBy is not ("date" or "title" or "path"))
            throw new InvalidDataException("Archives.SortBy は date、title、path のいずれかです。");
        if (notes.Count == 0) return;
        IOrderedEnumerable<Note> Sort(IEnumerable<Note> values) => options.SortBy switch
        {
            "title" => options.Descending
                ? values.OrderByDescending(Title, StringComparer.OrdinalIgnoreCase)
                : values.OrderBy(Title, StringComparer.OrdinalIgnoreCase),
            "path" => options.Descending
                ? values.OrderByDescending(note => note.SourcePath, StringComparer.OrdinalIgnoreCase)
                : values.OrderBy(note => note.SourcePath, StringComparer.OrdinalIgnoreCase),
            _ => options.Descending
                ? values.OrderByDescending(note => note.Metadata.Date).ThenBy(Title, StringComparer.OrdinalIgnoreCase)
                : values.OrderBy(note => note.Metadata.Date).ThenBy(Title, StringComparer.OrdinalIgnoreCase)
        };
        AddPage(options.IndexPath, "記事一覧", Sort(notes));
        foreach (var tag in notes.SelectMany(note => note.Metadata.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = $"{options.TagDirectory.TrimEnd('/')}/{Uri.EscapeDataString(tag)}/index.html";
            AddPage(path, $"タグ: {tag}", Sort(notes.Where(note => (note.Metadata.Tags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase))));
        }
        foreach (var year in notes.Where(note => note.Metadata.Date.HasValue).Select(note => note.Metadata.Date!.Value.Year).Distinct().OrderByDescending(value => value))
            AddPage($"{options.DateDirectory.TrimEnd('/')}/{year}/index.html", $"{year}年の記事", Sort(notes.Where(note => note.Metadata.Date?.Year == year)));

        void AddPage(string path, string title, IEnumerable<Note> pageNotes)
        {
            _ = PathSafety.OutputFile(output, path);
            var from = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(from)) from = ".";
            var hrefs = pageNotes.Select(note =>
            {
                var route = routes[Path.GetFullPath(note.SourcePath)];
                var href = Path.GetRelativePath(from, route.Replace('/', Path.DirectorySeparatorChar)).Replace(Path.DirectorySeparatorChar, '/');
                href = string.Join('/', href.Split('/').Select(Uri.EscapeDataString));
                var date = note.Metadata.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
                return $"<li><a href=\"{href}\">{WebUtility.HtmlEncode(Title(note))}</a> <time>{date}</time></li>";
            });
            var stylesheet = string.Concat(Enumerable.Repeat("../", path.Count(c => c == '/'))) + "style.css";
            var html = $"<!doctype html><html lang=\"ja\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>{WebUtility.HtmlEncode(title)}</title><link rel=\"stylesheet\" href=\"{stylesheet}\"><main><h1>{WebUtility.HtmlEncode(title)}</h1><ul>{string.Join("\n", hrefs)}</ul></main></html>";
            if (!generated.TryAdd(path, Encoding.UTF8.GetBytes(html))) throw new InvalidDataException($"アーカイブ出力先が重複しています: {path}");
        }
    }

    private static string Title(Note note) => note.Metadata.Title ?? Path.GetFileNameWithoutExtension(note.SourcePath);
}
