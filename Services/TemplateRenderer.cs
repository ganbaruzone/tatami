using System;
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

    public TemplateRenderer(string templatePath)
    {
        _template = Template.Parse(File.ReadAllText(templatePath), templatePath);
        if (_template.HasErrors) throw new InvalidDataException(_template.Messages.ToString());
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        // Arbitrary attributes can inject event handlers; media embeds are outside the MVP.
        builder.Extensions.RemoveAll(e => e is GenericAttributesExtension or MediaLinkExtension);
        _pipeline = builder.Build();
    }

    private string RenderMarkdown(string markdown)
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
            return url;
        };
        renderer.Render(Markdown.Parse(PrivateContentFilter.Remove(markdown), _pipeline));
        return writer.ToString();
    }

    public string Render(Note note)
    {
        var globals = new ScriptObject
        {
            ["title"] = WebUtility.HtmlEncode(note.Metadata.Title ?? Path.GetFileNameWithoutExtension(note.SourcePath)),
            ["date"] = note.Metadata.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            ["tags"] = (note.Metadata.Tags ?? []).Select(WebUtility.HtmlEncode).ToArray(),
            ["content"] = RenderMarkdown(note.Markdown)
        };
        var context = new TemplateContext { StrictVariables = true };
        context.PushGlobal(globals);
        return _template.Render(context);
    }
}
