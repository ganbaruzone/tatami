using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tatami.Models;

namespace Tatami.Services;

public sealed class SiteBuilder(string? templateDirectory = null)
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private readonly string _templates = templateDirectory ?? Path.Combine(AppContext.BaseDirectory, "Templates");

    public Task<BuildResult> BuildAsync(SiteConfig config, CancellationToken cancellationToken = default) =>
        BuildAsync(config.InputPath, config.OutputPath, cancellationToken);

    public async Task<BuildResult> BuildAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        await BuildLock.WaitAsync(cancellationToken);
        try { return await Task.Run(() => Build(inputPath, outputPath, cancellationToken), cancellationToken); }
        finally { BuildLock.Release(); }
    }

    private BuildResult Build(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputPath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException($"入力フォルダがありません: {input}");
        if (PathSafety.IsWithin(input, output)) throw new InvalidDataException("出力先に入力元と同じフォルダ、またはその親は指定できません。");
        PathSafety.RejectLinks(input);
        PathSafety.RejectLinks(output);
        var renderer = new TemplateRenderer(Path.Combine(_templates, "default.html"));
        var generated = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var published = 0;
        var unpublished = 0;
        foreach (var note in new NoteRepository().ReadAll(input, output))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PublishFilter.IsPublic(note)) { unpublished++; continue; }
            var slug = note.Metadata.Slug is null
                ? PathSafety.FileSlug(Path.GetFileNameWithoutExtension(note.SourcePath))
                : PathSafety.Slug(note.Metadata.Slug);
            var relative = slug + "/index.html";
            if (!generated.TryAdd(relative, Encoding.UTF8.GetBytes(renderer.Render(note))))
                throw new InvalidDataException($"slugが重複しています: {slug}");
            published++;
        }
        generated.Add("style.css", File.ReadAllBytes(Path.Combine(_templates, "style.css")));
        GeneratedSiteWriter.Publish(output, generated, cancellationToken);
        return new BuildResult(published, unpublished, output);
    }

}
