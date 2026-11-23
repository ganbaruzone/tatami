using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Tatami.Services;

public sealed record ImportedAttachment(string Path, string RelativePath, string FileName, bool IsImage, string Markdown);
public sealed record NoteAttachment(string Path, string OutputPath);

/// <summary>Imports and resolves workspace-owned files linked from Markdown notes.</summary>
public sealed class AttachmentService
{
    public const string FolderName = ".tatami-assets";
    public const long MaximumFileSizeBytes = 100L * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp" };
    private static readonly HashSet<string> SupportedExtensions = new(ImageExtensions, StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".mp3", ".wav", ".ogg", ".mp4", ".webm" };
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public ImportedAttachment Import(string workspace, string notePath, string sourcePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        notePath = Path.GetFullPath(notePath);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!PathSafety.IsWithin(notePath, root) || !Path.GetExtension(notePath).Equals(".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("添付先ノートはメモのディレクトリ内に指定してください。");
        PathSafety.RejectLinks(notePath);
        if (!File.Exists(notePath)) throw new FileNotFoundException("添付先ノートが見つかりません。", notePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("添付ファイルが見つかりません。", sourcePath);
        PathSafety.RejectLinks(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension))
            throw new InvalidDataException("添付できる形式は画像（PNG/JPEG/GIF/WebP/AVIF/BMP）、PDF、音声（MP3/WAV/OGG）、動画（MP4/WebM）です。");
        var info = new FileInfo(sourcePath);
        if (info.Length > MaximumFileSizeBytes) throw new InvalidDataException("添付ファイルは100 MB以下にしてください。");

        var assetRoot = Path.Combine(root, FolderName);
        PathSafety.RejectLinks(assetRoot);
        Directory.CreateDirectory(assetRoot);
        var sourceFileName = Path.GetFileName(sourcePath);
        var fileName = CreateSafeName(sourceFileName, extension);
        var assetPath = Path.Combine(assetRoot, $"{Guid.NewGuid():N}-{fileName}");
        var temporary = Path.Combine(assetRoot, $".tatami-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, temporary, overwrite: false);
            PathSafety.RejectLinks(assetPath);
            File.Move(temporary, assetPath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        var relative = Path.GetRelativePath(Path.GetDirectoryName(notePath)!, assetPath).Replace(Path.DirectorySeparatorChar, '/');
        var encoded = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        var isImage = ImageExtensions.Contains(extension);
        var label = sourceFileName.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
        var markdown = $"{(isImage ? "!" : "")}[{label}](<{encoded}>)";
        return new ImportedAttachment(assetPath, relative, fileName, isImage, markdown);
    }

    public IReadOnlyList<NoteAttachment> FindReferenced(string workspace, string notePath, string markdown)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var assetRoot = Path.GetFullPath(Path.Combine(root, FolderName));
        var document = Markdown.Parse(markdown, Pipeline);
        var found = new Dictionary<string, NoteAttachment>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var link in FindLinks(document))
        {
            if (!TryGetManagedPath(root, notePath, link.Url ?? "", out var path)) continue;
            PathSafety.RejectLinks(assetRoot);
            PathSafety.RejectLinks(path);
            if (!IsSupported(path))
                throw new InvalidDataException($"サポートされていない添付形式です: {path}");
            if (!File.Exists(path)) throw new FileNotFoundException($"ノートが参照する添付ファイルがありません: {link.Url}", path);
            if (new FileInfo(path).Length > MaximumFileSizeBytes) throw new InvalidDataException($"添付ファイルが100 MBを超えています: {path}");
            var relativeAsset = Path.GetRelativePath(assetRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            var outputPath = "assets/" + relativeAsset;
            PathSafety.OutputFile(root, outputPath);
            found[path] = new NoteAttachment(path, outputPath);
        }
        return found.Values.ToArray();
    }

    public string RewriteManagedLinksForOutput(string workspace, string notePath, string markdown,
        string outputPageRoute, IReadOnlyDictionary<string, string> assetRoutes)
    {
        var edits = new List<(int Start, int Length, string Value)>();
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in FindLinks(document))
        {
            if (link.UrlSpan.IsEmpty || !TryGetManagedPath(workspace, notePath, link.Url ?? "", out var path)
                || !assetRoutes.TryGetValue(path, out var route)) continue;
            var suffix = UrlSuffix(link.Url!);
            edits.Add((link.UrlSpan.Start, link.UrlSpan.Length, RelativeOutputUrl(outputPageRoute, route) + suffix));
        }
        markdown = ApplyEdits(markdown, edits);

        document = Markdown.Parse(markdown, Pipeline);
        edits.Clear();
        foreach (var definition in DescendantBlocks(document).OfType<LinkReferenceDefinitionGroup>()
            .SelectMany(group => group.Links.Values))
        {
            if (string.IsNullOrWhiteSpace(definition.Url)
                || !TryGetManagedPath(workspace, notePath, definition.Url, out var path)
                || !assetRoutes.TryGetValue(path, out var route)) continue;
            var span = definition.Span;
            if (span.Start < 0 || span.End >= markdown.Length) continue;
            var raw = markdown.Substring(span.Start, span.Length);
            var oldDestination = definition.UrlHasPointyBrackets ? $"<{definition.Url}>" : definition.Url;
            var newRoute = RelativeOutputUrl(outputPageRoute, route) + UrlSuffix(definition.Url!);
            var newDestination = definition.UrlHasPointyBrackets ? $"<{newRoute}>" : newRoute;
            var relativeStart = raw.IndexOf(oldDestination!, StringComparison.Ordinal);
            if (relativeStart >= 0) edits.Add((span.Start + relativeStart, oldDestination.Length, newDestination));
        }
        return ApplyEdits(markdown, edits);
    }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));
    public static bool IsManagedPath(string workspace, string path) => PathSafety.IsWithin(path, Path.Combine(Path.GetFullPath(workspace), FolderName));
    public static bool TryGetManagedPath(string workspace, string notePath, string url, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || IsExternal(url)) return false;
        var suffixIndex = url.IndexOfAny(['#', '?']);
        var pathPart = suffixIndex >= 0 ? url[..suffixIndex] : url;
        if (pathPart.Length == 0) return false;
        try
        {
            path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(notePath)!,
                Uri.UnescapeDataString(pathPart).Replace('/', Path.DirectorySeparatorChar)));
            return IsManagedPath(workspace, path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }
    public static string LivePreviewToken(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24].ToLowerInvariant();

    private static IEnumerable<LinkInline> FindLinks(MarkdownDocument document)
    {
        foreach (var block in DescendantBlocks(document))
            if (block is LeafBlock { Inline: ContainerInline inline })
                foreach (var link in DescendantLinks(inline)) yield return link;
    }

    private static IEnumerable<Block> DescendantBlocks(ContainerBlock container)
    {
        foreach (var block in container)
        {
            yield return block;
            if (block is ContainerBlock child)
                foreach (var nested in DescendantBlocks(child)) yield return nested;
        }
    }

    private static IEnumerable<LinkInline> DescendantLinks(ContainerInline inline)
    {
        for (var current = inline.FirstChild; current != null; current = current.NextSibling)
        {
            if (current is LinkInline link) yield return link;
            if (current is ContainerInline child)
                foreach (var nested in DescendantLinks(child)) yield return nested;
        }
    }

    private static bool IsExternal(string url) => url.StartsWith("//", StringComparison.Ordinal)
        || Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme);

    private static string RelativeOutputUrl(string pageRoute, string assetRoute)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(pageRoute)!, assetRoute).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        return string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    }

    private static string UrlSuffix(string url)
    {
        var suffixIndex = url.IndexOfAny(['#', '?']);
        return suffixIndex >= 0 ? url[suffixIndex..] : string.Empty;
    }

    private static string ApplyEdits(string text, List<(int Start, int Length, string Value)> edits)
    {
        foreach (var edit in edits.OrderByDescending(item => item.Start))
            text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        return text;
    }

    private static string CreateSafeName(string name, string extension)
    {
        var stem = Path.GetFileNameWithoutExtension(name).Normalize(NormalizationForm.FormC);
        var safe = new string(stem.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ' or '.' ? character : '_').ToArray())
            .Trim(' ', '.');
        if (safe.Length > 80) safe = safe[..80].TrimEnd(' ', '.');
        if (safe.Length == 0) safe = "attachment";
        return safe + extension.ToLowerInvariant();
    }
}
