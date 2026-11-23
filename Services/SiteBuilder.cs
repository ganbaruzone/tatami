using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        BuildAsync(config.InputPath, config.OutputPath, config.Archives, cancellationToken);

    public Task<BuildResult> BuildAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
        BuildAsync(inputPath, outputPath, new SiteArchiveOptions(), cancellationToken);

    private async Task<BuildResult> BuildAsync(string inputPath, string outputPath, SiteArchiveOptions archives, CancellationToken cancellationToken)
    {
        await BuildLock.WaitAsync(cancellationToken);
        try { return await Task.Run(() => Build(inputPath, outputPath, archives, cancellationToken), cancellationToken); }
        finally { BuildLock.Release(); }
    }

    private BuildResult Build(string inputPath, string outputPath, SiteArchiveOptions archives, CancellationToken cancellationToken)
    {
        var input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputPath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException($"入力フォルダがありません: {input}");
        if (PathSafety.IsWithin(input, output)) throw new InvalidDataException("出力先に入力元と同じフォルダ、またはその親は指定できません。");
        PathSafety.RejectLinks(input);
        PathSafety.RejectLinks(output);
        var notes = new NoteRepository().ReadAll(input, output).ToArray();
        var routes = new NoteLinkService().BuildPublishedRoutes(notes);
        var generated = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var assetRoutes = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var assetOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attachments = new AttachmentService();
        foreach (var note in notes.Where(PublishFilter.IsPublic))
        {
            var publicMarkdown = PrivateContentFilter.Remove(note.Markdown);
            foreach (var asset in attachments.FindReferenced(input, note.SourcePath, publicMarkdown))
            {
                if (assetOwners.TryGetValue(asset.OutputPath, out var owner)
                    && !string.Equals(owner, asset.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException($"添付ファイルの出力先が重複しています: {asset.OutputPath}");
                assetOwners[asset.OutputPath] = asset.Path;
                var bytes = File.ReadAllBytes(asset.Path);
                if (generated.TryGetValue(asset.OutputPath, out var existing) && !existing.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException($"添付ファイルの出力先が重複しています: {asset.OutputPath}");
                generated[asset.OutputPath] = bytes;
                assetRoutes[asset.Path] = asset.OutputPath;
            }
        }
        var renderer = new TemplateRenderer(Path.Combine(_templates, "default.html"), routes, assetRoutes);
        var published = 0;
        var unpublished = 0;
        foreach (var note in notes)
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
        ArchivePageGenerator.Add(notes.Where(PublishFilter.IsPublic).ToArray(), routes, archives, generated, output);
        generated.Add("style.css", File.ReadAllBytes(Path.Combine(_templates, "style.css")));
        GeneratedSiteWriter.Publish(output, generated, cancellationToken);
        return new BuildResult(published, unpublished, output);
    }

}
