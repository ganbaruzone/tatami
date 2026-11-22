using System;
using System.IO;
using System.Linq;
using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Syntax;

namespace Tatami.Services;

public static class PrivateContentFilter
{
    // A first parse finds boundaries using Markdig, before references/footnotes are resolved
    // by the full rendering pipeline. Blank source ranges so private definitions cannot leak.
    private static readonly MarkdownPipeline BoundaryPipeline = new MarkdownPipelineBuilder()
        .UseCustomContainers().DisableHtml().Build();

    public static string Remove(string markdown)
    {
        var document = Markdown.Parse(markdown, BoundaryPipeline);
        var privateBlocks = document.Descendants<CustomContainer>()
            .Where(block => string.Equals(block.Info, "private", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (privateBlocks.Length == 0) return markdown;
        var characters = markdown.ToCharArray();
        foreach (var block in privateBlocks)
        {
            if (block.ClosingFencedCharCount == 0)
                throw new InvalidDataException($"非公開ブロック（{block.Line + 1}行目）が閉じられていません。::: で閉じてください。");
            for (var i = block.Span.Start; i <= block.Span.End && i < characters.Length; i++)
                if (characters[i] is not ('\r' or '\n')) characters[i] = ' ';
        }
        return new string(characters);
    }
}
