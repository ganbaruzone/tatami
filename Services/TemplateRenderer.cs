using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Extensions.MediaLinks;
using Markdig.Renderers;
using Scriban;
using Scriban.Runtime;
using Tatami.Models;

namespace Tatami.Services;

public sealed class TemplateRenderer
{
    private readonly Template _template;
    private readonly MarkdownPipeline _pipeline;
    private readonly IReadOnlyDictionary<string, string> _noteRoutes;
    private readonly IReadOnlyDictionary<string, string> _assetRoutes;
    private readonly bool _rootRelativeNoteLinks;

    public TemplateRenderer(string templatePath, IReadOnlyDictionary<string, string>? noteRoutes = null,
        IReadOnlyDictionary<string, string>? assetRoutes = null, bool rootRelativeNoteLinks = false)
    {
        _noteRoutes = noteRoutes ?? new Dictionary<string, string>();
        _assetRoutes = assetRoutes ?? new Dictionary<string, string>();
        _rootRelativeNoteLinks = rootRelativeNoteLinks;
        _template = Template.Parse(File.ReadAllText(templatePath), templatePath);
        if (_template.HasErrors) throw new InvalidDataException(_template.Messages.ToString());
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        // Arbitrary attributes can inject event handlers; media embeds are outside the MVP.
        builder.Extensions.RemoveAll(e => e is GenericAttributesExtension or MediaLinkExtension);
        _pipeline = builder.Build();
    }

    private string RenderMarkdown(Note note)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.LinkRewriter = url =>
        {
            // Allow web/mail links and ordinary relative URLs, but no executable/file schemes.
            if (url.Any(char.IsControl) || url.Contains('\\')) return "#";
            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)
                && absolute.Scheme is not ("https" or "http" or "mailto")) return "#";
            return RewriteNoteLink(note, url);
        };
        renderer.Render(Markdown.Parse(PrivateContentFilter.Remove(note.Markdown), _pipeline));
        return writer.ToString();
    }

    private string RewriteNoteLink(Note note, string url)
    {
        var suffixIndex = url.IndexOfAny(['#', '?']);
        var pathPart = suffixIndex >= 0 ? url[..suffixIndex] : url;
        var suffix = suffixIndex >= 0 ? url[suffixIndex..] : string.Empty;
        string targetPath;
        try
        {
            targetPath = pathPart.Length == 0
                ? Path.GetFullPath(note.SourcePath)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(note.SourcePath)!,
                    Uri.UnescapeDataString(pathPart).Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return url; }
        if (!_noteRoutes.TryGetValue(targetPath, out var targetRoute)
            && !_assetRoutes.TryGetValue(targetPath, out targetRoute)) return url;

        var currentRoute = _noteRoutes.TryGetValue(Path.GetFullPath(note.SourcePath), out var publishedRoute)
            ? publishedRoute
            : $"{PathSafety.FileSlug(Path.GetFileNameWithoutExtension(note.SourcePath))}/index.html";
        if (_rootRelativeNoteLinks) return "/" + targetRoute + suffix;
        var relative = Path.GetRelativePath(Path.GetDirectoryName(currentRoute)!, targetRoute).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        return string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)) + suffix;
    }

    public string Render(Note note)
    {
        var globals = new ScriptObject
        {
            ["title"] = WebUtility.HtmlEncode(note.Metadata.Title ?? Path.GetFileNameWithoutExtension(note.SourcePath)),
            ["date"] = note.Metadata.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            ["tags"] = (note.Metadata.Tags ?? []).Select(WebUtility.HtmlEncode).ToArray(),
            ["content"] = RenderMarkdown(note)
        };
        var context = new TemplateContext { StrictVariables = true };
        context.PushGlobal(globals);
        return _template.Render(context);
    }
}
