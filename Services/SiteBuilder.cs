using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tatami.Models;

namespace Tatami.Services;

public sealed class SiteBuilder
{
    private const string ManifestName = ".tatami-manifest.json";
    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private readonly string _templates;

    public SiteBuilder(string? templateDirectory = null) =>
        _templates = templateDirectory ?? Path.Combine(AppContext.BaseDirectory, "Templates");

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
        var manifestPath = Path.Combine(output, ManifestName);
        PathSafety.RejectLinks(manifestPath);
        var oldManifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException("生成物一覧が空です。")
            : new Dictionary<string, string>();
        var old = new Dictionary<string, string>(oldManifest, StringComparer.OrdinalIgnoreCase);
        foreach (var relative in generated.Keys)
        {
            var previous = old.Keys.FirstOrDefault(key => string.Equals(key, relative, StringComparison.OrdinalIgnoreCase));
            if (previous != null && previous != relative)
                throw new InvalidDataException($"slugの大文字・小文字だけの変更はできません: {previous} → {relative}。別のslugまたは新しい出力先を使用してください。");
        }
        var touched = old.Keys.Union(generated.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var backups = new Dictionary<string, byte[]?>();
        // Validate every target before changing any output. Only unchanged, owned files may be replaced/deleted.
        foreach (var relative in touched)
        {
            var path = PathSafety.OutputFile(output, relative);
            if (Directory.Exists(path)) throw new IOException($"出力ファイルと同名のフォルダがあります: {path}");
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                if (!old.TryGetValue(relative, out var expected) || Hash(bytes) != expected)
                    throw new IOException($"既存ファイルを保護するためビルドを中止しました: {path}");
                backups[path] = bytes;
            }
            else backups[path] = null;
        }
        if (Directory.Exists(manifestPath)) throw new IOException("生成物一覧と同名のフォルダがあります。");
        backups[manifestPath] = File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null;
        // Check parent paths, including file/directory collisions, before starting the transaction.
        foreach (var path in backups.Keys)
            for (var parent = Path.GetDirectoryName(path); parent != null && PathSafety.IsWithin(parent, output); parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) throw new IOException($"出力先に同名のファイルがあります: {parent}");
        cancellationToken.ThrowIfCancellationRequested();
        var changed = new List<string>();
        try
        {
            foreach (var relative in touched)
            {
                var path = PathSafety.OutputFile(output, relative);
                if (generated.TryGetValue(relative, out var bytes)) WriteAtomically(path, bytes);
                else if (File.Exists(path)) File.Delete(path);
                changed.Add(path);
            }
            WriteAtomically(manifestPath, JsonSerializer.SerializeToUtf8Bytes(
                generated.ToDictionary(x => x.Key, x => Hash(x.Value)),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            foreach (var path in changed.AsEnumerable().Reverse())
            {
                try
                {
                    if (backups[path] is { } bytes) WriteAtomically(path, bytes);
                    else File.Delete(path);
                }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            if (errors.Count > 1) throw new AggregateException("出力の復元に失敗しました。出力先を確認してください。", errors);
            throw;
        }
        return new BuildResult(published, unpublished, output);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteAtomically(string path, byte[] bytes)
    {
        PathSafety.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".tatami-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
