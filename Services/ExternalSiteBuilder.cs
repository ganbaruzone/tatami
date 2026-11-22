using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tatami.Models;

namespace Tatami.Services;

public sealed class ExternalSiteBuilder
{
    public Task<BuildResult> BuildAsync(SiteConfig config, IProgress<string>? log = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => BuildCoreAsync(config, log, cancellationToken), cancellationToken);

    private static async Task<BuildResult> BuildCoreAsync(SiteConfig config, IProgress<string>? log, CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(config.InputPath);
        var output = Path.GetFullPath(config.OutputPath);
        var options = config.External;
        if (string.IsNullOrWhiteSpace(options.ProjectPath) || string.IsNullOrWhiteSpace(options.Executable))
            throw new InvalidDataException("外部ビルダーのプロジェクトと実行ファイルを設定してください。");
        var project = Path.GetFullPath(options.ProjectPath);
        if (!Directory.Exists(input) || !Directory.Exists(project)) throw new DirectoryNotFoundException("メモまたは外部プロジェクトが見つかりません。");
        if (PathSafety.IsWithin(input, output) || PathSafety.IsWithin(project, output))
            throw new InvalidDataException("出力先はメモやプロジェクトと同じ場所、またはその親にできません。");
        if (string.Equals(Path.TrimEndingDirectorySeparator(input), Path.TrimEndingDirectorySeparator(project), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("メモのディレクトリと外部プロジェクトは別に指定してください。");
        if (options.TimeoutSeconds is < 1 or > 86400) throw new InvalidDataException("タイムアウトは1〜86400秒で指定してください。");
        PathSafety.RejectLinks(input);
        PathSafety.RejectLinks(output);
        var temp = Path.GetTempPath();
        // macOS exposes /var through a symlink; use its physical spelling for path checks.
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/", StringComparison.Ordinal)) temp = "/private" + temp;
        var staging = Path.Combine(temp, "tatami-build-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(staging, "content");
        var generatedOutput = Path.Combine(staging, "site");
        Directory.CreateDirectory(content);
        Directory.CreateDirectory(generatedOutput);
        try
        {
            var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var privateCount = 0;
            var excluded = PathSafety.IsWithin(project, input) ? new[] { project } : [];
            foreach (var note in new NoteRepository().ReadAll(input, output, excluded))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PublishFilter.IsPublic(note)) { privateCount++; continue; }
                var slug = note.Metadata.Slug is null ? PathSafety.FileSlug(Path.GetFileNameWithoutExtension(note.SourcePath)) : PathSafety.Slug(note.Metadata.Slug);
                if (!slugs.Add(slug)) throw new InvalidDataException($"slugが重複しています: {slug}");
                var body = PrivateContentFilter.Remove(note.Markdown);
                // Keep custom front matter for the user's generator. Original notes are never changed.
                await File.WriteAllTextAsync(Path.Combine(content, slug + ".md"), $"---\n{note.FrontMatter}---\n{body}", cancellationToken);
            }
            log?.Report($"公開ノート {slugs.Count} 件を準備しました（非公開 {privateCount} 件を除外）。");
            var start = new ProcessStartInfo
            {
                FileName = options.Executable,
                WorkingDirectory = project,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in options.Arguments)
                start.ArgumentList.Add(argument.Replace("{content}", content, StringComparison.Ordinal).Replace("{output}", generatedOutput, StringComparison.Ordinal));
            start.Environment["TATAMI_CONTENT_DIR"] = content;
            start.Environment["TATAMI_OUTPUT_DIR"] = generatedOutput;
            start.Environment["CI"] = "1";
            if (Path.IsPathFullyQualified(options.Executable))
                start.Environment["PATH"] = Path.GetDirectoryName(options.Executable) + Path.PathSeparator + start.Environment["PATH"];
            log?.Report($"実行: {options.Executable}\nプロジェクト: {project}");
            using var process = new Process { StartInfo = start };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            if (!process.Start()) throw new IOException("外部ビルダーを起動できません。");
            var stdout = ReadLogAsync(process.StandardOutput, log, "", timeout.Token);
            var stderr = ReadLogAsync(process.StandardError, log, "[stderr] ", timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException($"外部ビルドが{options.TimeoutSeconds}秒でタイムアウトしました。");
            }
            if (process.ExitCode != 0) throw new IOException($"外部ビルドが終了コード {process.ExitCode} で失敗しました。ログを確認してください。");
            var generated = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            ReadGenerated(generatedOutput, generatedOutput, generated, cancellationToken);
            if (!generated.Keys.Any(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("生成HTMLがありません。ビルダーが TATAMI_OUTPUT_DIR（または {output}）へ出力するよう設定してください。");
            GeneratedSiteWriter.Publish(output, generated, cancellationToken);
            log?.Report($"生成物 {generated.Count} ファイルを反映しました。");
            return new BuildResult(slugs.Count, privateCount, output);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static async Task ReadLogAsync(StreamReader reader, IProgress<string>? log, string prefix, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line) log?.Report(prefix + line);
    }

    private static void ReadGenerated(string root, string directory, Dictionary<string, byte[]> files, CancellationToken token)
    {
        PathSafety.RejectLinks(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            PathSafety.RejectLinks(path);
            if (Directory.Exists(path)) ReadGenerated(root, path, files, token);
            else
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                PathSafety.OutputFile(root, relative);
                if (!files.TryAdd(relative, File.ReadAllBytes(path))) throw new InvalidDataException($"生成パスが重複しています: {relative}");
            }
        }
    }
}
