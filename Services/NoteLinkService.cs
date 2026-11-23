using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Tatami.Models;

namespace Tatami.Services;

public sealed record NoteLinkTarget(string Path, string Title, string? HeadingId = null, string? HeadingTitle = null)
{
    public string Display => HeadingId == null ? Title : $"{Title}  ›  {HeadingTitle}";
}

public sealed record BrokenNoteLink(string SourcePath, int Line, string Url, string Reason);
public sealed record IncomingNoteReference(string SourcePath, int Line, string Excerpt, bool IsLink);

/// <summary>Resolves and creates portable Markdown links between workspace notes.</summary>
public sealed class NoteLinkService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().DisableHtml().Build();

    public IReadOnlyList<NoteLinkTarget> FindTargets(string workspace)
    {
        var targets = new List<NoteLinkTarget>();
        foreach (var path in EnumerateNotes(workspace))
        {
            var text = File.ReadAllText(path);
            var (body, title) = GetBodyAndTitle(path, text);
            targets.Add(new NoteLinkTarget(path, title));
            var document = Markdown.Parse(body, Pipeline);
            foreach (var heading in Descendants<HeadingBlock>(document))
            {
                var id = heading.GetAttributes().Id;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var source = heading.Span;
                var raw = source.Start >= 0 && source.End < body.Length
                    ? body.Substring(source.Start, source.Length)
                    : string.Empty;
                var headingTitle = Markdown.ToPlainText(raw, Pipeline).Trim();
                if (headingTitle.Length == 0) headingTitle = id;
                targets.Add(new NoteLinkTarget(path, title, id, headingTitle));
            }
        }
        return targets;
    }

    public IReadOnlyDictionary<string, string> BuildPublishedRoutes(IEnumerable<Note> notes)
    {
        var routes = new Dictionary<string, string>(PathComparer);
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            if (!PublishFilter.IsPublic(note)) continue;
            var slug = note.Metadata.Slug is null
                ? PathSafety.FileSlug(Path.GetFileNameWithoutExtension(note.SourcePath))
                : PathSafety.Slug(note.Metadata.Slug);
            var outputPath = $"{slug}/index.html";
            if (!outputPaths.Add(outputPath)) throw new InvalidDataException($"slugが重複しています: {slug}");
            if (!routes.TryAdd(Path.GetFullPath(note.SourcePath), outputPath))
                throw new InvalidDataException($"slugが重複しています: {slug}");
        }
        return routes;
    }

    public IReadOnlyList<IncomingNoteReference> FindIncomingReferences(string workspace, string targetPath,
        IReadOnlyDictionary<string, string>? openDocuments = null)
    {
        var root = Path.GetFullPath(workspace);
        var target = Path.GetFullPath(targetPath);
        var notes = EnumerateNotes(root).ToArray();
        var known = new HashSet<string>(notes, PathComparer);
        if (!known.Contains(target)) return [];
        var targetName = Path.GetFileNameWithoutExtension(target);
        var targetTitle = GetBodyAndTitle(target, ReadDocument(target, openDocuments)).Title;
        var terms = new[] { targetTitle, targetName }
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length).ToArray();
        var results = new List<IncomingNoteReference>();
        foreach (var sourcePath in notes)
        {
            if (string.Equals(sourcePath, target, PathComparison)) continue;
            var text = ReadDocument(sourcePath, openDocuments);
            var body = GetBodyAndTitle(sourcePath, text).Body;
            var document = Markdown.Parse(body, Pipeline);
            var links = FindLinks(document).ToArray();
            foreach (var link in links)
            {
                if (link.IsImage || string.IsNullOrWhiteSpace(link.Url) || IsExternal(link.Url)) continue;
                if (TryResolve(root, sourcePath, link.Url, known, out var resolved, out _, out _)
                    && string.Equals(resolved, target, PathComparison))
                    results.Add(new IncomingNoteReference(sourcePath, GetLine(body, link.Span.Start),
                        GetExcerpt(body, link.Span.Start), true));
            }

            var excluded = links.Select(link => link.Span).Where(span => span.Start >= 0).ToArray();
            foreach (var block in Descendants<CodeBlock>(document))
                if (block.Span.Start >= 0) excluded = [.. excluded, block.Span];
            foreach (var code in Descendants<CodeInline>(document))
                if (code.Span.Start >= 0) excluded = [.. excluded, code.Span];
            var mentionSpans = new List<(int Start, int End)>();
            foreach (var term in terms)
            {
                var offset = 0;
                while ((offset = body.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var end = offset + term.Length;
                    if (!excluded.Any(span => offset <= span.End && end - 1 >= span.Start)
                        && !mentionSpans.Any(span => offset <= span.End && end - 1 >= span.Start))
                    {
                        results.Add(new IncomingNoteReference(sourcePath, GetLine(body, offset), GetExcerpt(body, offset), false));
                        mentionSpans.Add((offset, end - 1));
                    }
                    offset = end;
                }
            }
        }
        return results.OrderBy(reference => reference.SourcePath, PathComparer).ThenBy(reference => reference.Line)
            .ThenByDescending(reference => reference.IsLink).ToArray();
    }

    private static string ReadDocument(string path, IReadOnlyDictionary<string, string>? openDocuments) =>
        openDocuments != null && openDocuments.TryGetValue(Path.GetFullPath(path), out var openText) ? openText : File.ReadAllText(path);

    private static string GetExcerpt(string text, int offset)
    {
        var safeOffset = Math.Clamp(offset, 0, text.Length);
        var start = safeOffset == 0 ? -1 : text.LastIndexOf('\n', safeOffset - 1);
        start++;
        var end = text.IndexOf('\n', safeOffset);
        if (end < 0) end = text.Length;
        return text[start..end].Trim();
    }

    public IReadOnlyList<BrokenNoteLink> CheckLinks(string workspace, IReadOnlyDictionary<string, string>? openDocuments = null)
    {
        var root = Path.GetFullPath(workspace);
        var notes = EnumerateNotes(root);
        var known = new HashSet<string>(notes, PathComparer);
        var headingIds = notes.ToDictionary(path => path, path =>
        {
            var text = openDocuments != null && openDocuments.TryGetValue(path, out var openText)
                ? openText : File.ReadAllText(path);
            return GetHeadingIds(GetBodyAndTitle(path, text).Body);
        }, PathComparer);
        var results = new List<BrokenNoteLink>();
        foreach (var sourcePath in notes)
        {
            var text = openDocuments != null && openDocuments.TryGetValue(sourcePath, out var openText)
                ? openText : File.ReadAllText(sourcePath);
            var (body, _) = GetBodyAndTitle(sourcePath, text);
            var frontMatterLineOffset = CountLines(text.AsSpan(0, Math.Max(0, text.Length - body.Length)));
            var document = Markdown.Parse(body, Pipeline);
            foreach (var link in FindLinks(document))
            {
                var url = link.Url;
                if (string.IsNullOrWhiteSpace(url) || IsExternal(url)) continue;
                if (AttachmentService.TryGetManagedPath(root, sourcePath, url, out var attachmentPath))
                {
                    string? attachmentError = null;
                    try
                    {
                        PathSafety.RejectLinks(attachmentPath);
                        if (!AttachmentService.IsSupported(attachmentPath)) attachmentError = "添付できないファイル形式です";
                        else if (!File.Exists(attachmentPath)) attachmentError = "添付ファイルがありません";
                    }
                    catch (IOException) { attachmentError = "添付ファイルのパスにシンボリックリンクがあります"; }
                    if (attachmentError != null)
                        results.Add(new BrokenNoteLink(sourcePath, GetLine(body, link.UrlSpan.Start) + frontMatterLineOffset, url, attachmentError));
                    continue;
                }
                if (link.IsImage) continue;
                if (!TryResolve(root, sourcePath, url, known, out var targetPath, out var fragment, out var reason))
                {
                    results.Add(new BrokenNoteLink(sourcePath, GetLine(body, link.UrlSpan.Start) + frontMatterLineOffset, url, reason));
                    continue;
                }
                if (fragment.Length > 0 && headingIds.TryGetValue(targetPath, out var ids) && !ids.Contains(fragment))
                    results.Add(new BrokenNoteLink(sourcePath, GetLine(body, link.UrlSpan.Start) + frontMatterLineOffset, url, $"見出し #{fragment} がありません"));
            }
        }
        return results;
    }

    public string CreateMarkdownLink(string sourcePath, NoteLinkTarget target, string label)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(sourcePath)!, target.Path).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        var destination = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        if (target.HeadingId != null) destination += "#" + Uri.EscapeDataString(target.HeadingId);
        label = label.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
        return $"[{label}](<{destination}>)";
    }

    /// <summary>Updates local inline links that point at a note being renamed.</summary>
    public string UpdateRenamedTargetLinks(string markdown, string sourcePath, string oldTargetPath, string newTargetPath)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var edits = new List<(int Start, int Length, string Value)>();
        foreach (var link in FindLinks(document))
        {
            if (link.IsImage || link.UrlSpan.IsEmpty || string.IsNullOrWhiteSpace(link.Url)
                || !TryLocalTarget(sourcePath, link.Url, out var resolved, out _)
                || !string.Equals(resolved, Path.GetFullPath(oldTargetPath), PathComparison)) continue;
            var oldUrl = link.Url;
            var newUrl = MakeRelativeUrl(sourcePath, newTargetPath, oldUrl);
            edits.Add((link.UrlSpan.Start, link.UrlSpan.Length, newUrl));
        }
        foreach (var edit in edits.OrderByDescending(item => item.Start))
            markdown = markdown.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);

        // CommonMark reference definitions share one destination across any number of references.
        markdown = UpdateReferenceDefinitions(markdown, sourcePath, oldTargetPath, newTargetPath);
        return markdown;
    }

    private static string UpdateMovedSourceLinks(string markdown, string oldSourcePath, string newSourcePath)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var edits = new List<(int Start, int Length, string Value)>();
        foreach (var link in FindLinks(document))
        {
            if (link.UrlSpan.IsEmpty || string.IsNullOrWhiteSpace(link.Url) || IsExternal(link.Url)
                || !TryLocalTarget(oldSourcePath, link.Url, out var resolved, out _)) continue;
            var rawUrl = markdown.Substring(link.UrlSpan.Start, link.UrlSpan.Length);
            if (!rawUrl.Equals(link.Url, StringComparison.Ordinal) && !rawUrl.Equals($"<{link.Url}>", StringComparison.Ordinal)) continue;
            edits.Add((link.UrlSpan.Start, link.UrlSpan.Length, MakeRelativeUrl(newSourcePath, resolved, link.Url)));
        }
        foreach (var group in Descendants<LinkReferenceDefinitionGroup>(document))
            foreach (var definition in group.Links.Values)
            {
                if (string.IsNullOrWhiteSpace(definition.Url) || !TryLocalTarget(oldSourcePath, definition.Url, out var resolved, out _)) continue;
                var block = definition.Span;
                if (block.Start < 0 || block.End >= markdown.Length) continue;
                var raw = markdown.Substring(block.Start, block.Length);
                var rawUrl = definition.UrlHasPointyBrackets ? $"<{definition.Url}>" : definition.Url;
                var replacement = MakeRelativeUrl(newSourcePath, resolved, definition.Url);
                if (definition.UrlHasPointyBrackets) replacement = $"<{replacement}>";
                var relativeStart = raw.IndexOf(rawUrl, StringComparison.Ordinal);
                if (relativeStart >= 0) edits.Add((block.Start + relativeStart, rawUrl.Length, replacement));
            }
        var nonOverlapping = new List<(int Start, int Length, string Value)>();
        var lastStart = markdown.Length;
        foreach (var edit in edits.OrderByDescending(item => item.Start))
        {
            if (edit.Start < 0 || edit.Start + edit.Length > lastStart || edit.Start + edit.Length > markdown.Length) continue;
            nonOverlapping.Add(edit);
            lastStart = edit.Start;
        }
        foreach (var edit in nonOverlapping)
            markdown = markdown.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        return markdown;
    }

    public IReadOnlyDictionary<string, string> PrepareRenameUpdates(string workspace, string oldTargetPath,
        string newTargetPath, IReadOnlyDictionary<string, string>? openDocuments = null)
    {
        var changes = new Dictionary<string, string>(PathComparer);
        foreach (var path in EnumerateNotes(workspace))
        {
            var text = openDocuments != null && openDocuments.TryGetValue(path, out var openText)
                ? openText : File.ReadAllText(path);
            var sourceForLinks = string.Equals(path, oldTargetPath, PathComparison) ? newTargetPath : path;
            var updated = string.Equals(path, oldTargetPath, PathComparison)
                ? UpdateMovedSourceLinks(text, oldTargetPath, newTargetPath)
                : text;
            updated = UpdateRenamedTargetLinks(updated, sourceForLinks, oldTargetPath, newTargetPath);
            if (!string.Equals(text, updated, StringComparison.Ordinal)) changes[path] = updated;
        }
        return changes;
    }

    public static string GetRelativePath(string sourcePath, string targetPath)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(sourcePath)!, targetPath).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        return string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    }

    private static string MakeRelativeUrl(string sourcePath, string newTargetPath, string oldUrl)
    {
        var suffixIndex = oldUrl.IndexOfAny(['#', '?']);
        var suffix = suffixIndex >= 0 ? oldUrl[suffixIndex..] : string.Empty;
        return GetRelativePath(sourcePath, newTargetPath) + suffix;
    }

    private static string UpdateReferenceDefinitions(string markdown, string sourcePath, string oldTargetPath, string newTargetPath)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var edits = new List<(int Start, int Length, string Value)>();
        foreach (var group in Descendants<LinkReferenceDefinitionGroup>(document))
            foreach (var definition in group.Links.Values)
            {
                if (string.IsNullOrWhiteSpace(definition.Url) || !TryLocalTarget(sourcePath, definition.Url, out var resolved, out _)
                    || !string.Equals(resolved, Path.GetFullPath(oldTargetPath), PathComparison)) continue;
                var block = definition.Span;
                if (block.Start < 0 || block.End >= markdown.Length) continue;
                var raw = markdown.Substring(block.Start, block.Length);
                var destination = definition.UrlHasPointyBrackets
                    ? $"<{MakeRelativeUrl(sourcePath, newTargetPath, definition.Url)}>"
                    : MakeRelativeUrl(sourcePath, newTargetPath, definition.Url);
                var rawUrl = definition.UrlHasPointyBrackets ? $"<{definition.Url}>" : definition.Url;
                var relativeStart = raw.IndexOf(rawUrl, StringComparison.Ordinal);
                if (relativeStart >= 0) edits.Add((block.Start + relativeStart, rawUrl.Length, destination));
            }
        foreach (var edit in edits.OrderByDescending(item => item.Start))
            markdown = markdown.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        return markdown;
    }

    private static bool TryResolve(string root, string sourcePath, string url, HashSet<string> known,
        out string targetPath, out string fragment, out string reason)
    {
        targetPath = string.Empty;
        reason = "リンク先ノートがありません";
        var hash = url.IndexOf('#');
        var pathPart = hash >= 0 ? url[..hash] : url;
        fragment = hash >= 0 ? Uri.UnescapeDataString(url[(hash + 1)..]) : string.Empty;
        var query = pathPart.IndexOf('?');
        if (query >= 0) pathPart = pathPart[..query];
        if (pathPart.Length == 0)
        {
            targetPath = Path.GetFullPath(sourcePath);
        }
        else if (!TryLocalTarget(sourcePath, pathPart, out targetPath, out reason)) return false;
        if (!PathSafety.IsWithin(targetPath, root)) { reason = "ワークスペース外へのリンクです"; return false; }
        if (known.Contains(targetPath)) return true;
        if (Path.GetExtension(targetPath).Length == 0 && known.Contains(targetPath + ".md"))
        {
            targetPath += ".md";
            return true;
        }
        reason = "リンク先ノートがありません";
        return false;
    }

    private static bool TryLocalTarget(string sourcePath, string url, out string targetPath, out string reason)
    {
        targetPath = string.Empty;
        reason = "リンク先が不正です";
        var hash = url.IndexOf('#');
        var query = url.IndexOf('?');
        var end = new[] { hash, query }.Where(i => i >= 0).DefaultIfEmpty(url.Length).Min();
        var pathPart = Uri.UnescapeDataString(url[..end]);
        if (pathPart.StartsWith("//", StringComparison.Ordinal) || pathPart.Contains('\\')) return false;
        try
        {
            targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, pathPart.Replace('/', Path.DirectorySeparatorChar)));
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsExternal(string url) => url.StartsWith("//", StringComparison.Ordinal)
        || Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme);

    private static HashSet<string> GetHeadingIds(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        return new HashSet<string>(Descendants<HeadingBlock>(document)
            .Select(heading => heading.GetAttributes().Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))!, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<T> Descendants<T>(ContainerBlock container) where T : MarkdownObject
    {
        foreach (var block in container)
        {
            if (block is T match) yield return match;
            if (block is ContainerBlock child)
                foreach (var nested in Descendants<T>(child)) yield return nested;
            if (block is LeafBlock { Inline: ContainerInline inline })
                foreach (var nested in InlineDescendants<T>(inline)) yield return nested;
        }
    }

    private static IEnumerable<T> InlineDescendants<T>(ContainerInline inline) where T : MarkdownObject
    {
        for (var current = inline.FirstChild; current != null; current = current.NextSibling)
        {
            if (current is T match) yield return match;
            if (current is ContainerInline child)
                foreach (var nested in InlineDescendants<T>(child)) yield return nested;
        }
    }

    private static IEnumerable<LinkInline> FindLinks(MarkdownDocument document) =>
        Descendants<Inline>(document).OfType<LinkInline>();

    private static IEnumerable<string> EnumerateNotes(string workspace)
    {
        var root = Path.GetFullPath(workspace);
        var files = new List<string>();
        void Visit(IEnumerable<WorkspaceEntry> nodes)
        {
            foreach (var node in nodes)
                if (node.IsDirectory) Visit(node.Children);
                else files.Add(Path.GetFullPath(node.FullPath));
        }
        Visit(WorkspaceEntry.Read(root));
        return files;
    }

    private static (string Body, string Title) GetBodyAndTitle(string path, string text)
    {
        try
        {
            var note = new NoteRepository().Parse(path, text);
            return (note.Markdown, note.Metadata.Title ?? Path.GetFileNameWithoutExtension(path));
        }
        catch (InvalidDataException) { return (text, Path.GetFileNameWithoutExtension(path)); }
    }

    private static int GetLine(string text, int offset)
    {
        if (offset <= 0) return 1;
        return 1 + text.Take(Math.Min(offset, text.Length)).Count(character => character == '\n');
    }

    private static int CountLines(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var character in text) if (character == '\n') count++;
        return count;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
