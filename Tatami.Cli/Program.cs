using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using Tatami.Models;
using Tatami.Services;

return await TatamiCli.RunAsync(args);

internal static class TatamiCli
{
    private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    private static bool _quiet;
    private static bool _verbose;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine($"tatami {Version}");
            return 0;
        }

        _quiet = args.Contains("--quiet");
        _verbose = args.Contains("--verbose");
        if (_quiet && _verbose) return UsageError("--quiet と --verbose は同時に指定できません。");
        var positional = new List<string>();
        string? input = null;
        string? output = null;
        string? configPath = null;
        string? directory = null;
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--quiet" or "--verbose") continue;
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is "--input" or "--output" or "--config" or "--directory")
            {
                var option = args[i];
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    return UsageError($"{option} には値を指定してください。");
                if (option == "--input") input = args[i];
                else if (option == "--output") output = args[i];
                else if (option == "--config") configPath = args[i];
                else directory = args[i];
                continue;
            }
            if (args[i].StartsWith('-')) return UsageError($"不明なオプションです: {args[i]}");
            positional.Add(args[i]);
        }

        try
        {
            if (positional.Count == 1 && positional[0] == "build")
            {
                var config = await ResolveConfigAsync(configPath);
                var effective = config with
                {
                    InputPath = input is null ? config.InputPath : Path.GetFullPath(input),
                    OutputPath = output is null ? config.OutputPath : Path.GetFullPath(output)
                };
                if (string.IsNullOrWhiteSpace(effective.InputPath) || string.IsNullOrWhiteSpace(effective.OutputPath))
                    return BuildUsageError(json, effective.OutputPath, "入力・出力ディレクトリが必要です。--input と --output を指定するか、設定してください。");
                var progress = _quiet ? null : new Progress<string>(message => Console.Error.WriteLine(message));
                var result = await new BuildCoordinator().BuildAsync(effective, progress);
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new { success = true, published = result.PublishedCount, privateCount = result.PrivateCount, output = result.OutputPath }));
                else if (!_quiet)
                    Console.WriteLine($"ビルド成功: 公開 {result.PublishedCount} 件、非公開 {result.PrivateCount} 件、出力先 {result.OutputPath}");
                return 0;
            }
            if (positional.Count == 3 && positional[0] == "notes" && positional[1] == "create")
            {
                var root = await ResolveInputAsync(input);
                var targetDirectory = directory is null
                    ? root
                    : Path.GetFullPath(Path.Combine(root, directory));
                var created = new NoteFileService(root).Create(targetDirectory, positional[2]);
                Console.WriteLine(Path.GetRelativePath(root, created));
                return 0;
            }
            if (positional.Count == 4 && positional[0] == "notes" && positional[1] == "rename")
            {
                var root = await ResolveInputAsync(input);
                var source = ResolveNotePath(root, positional[2]);
                var renamed = new NoteFileService(root).Rename(source, positional[3]);
                Console.WriteLine(Path.GetRelativePath(root, renamed));
                return 0;
            }
            if (positional.Count == 3 && positional[0] == "notes" && positional[1] == "trash" && positional[2] == "list")
            {
                var root = await ResolveInputAsync(input);
                var entries = new NoteFileService(root).ListTrash();
                foreach (var entry in entries)
                    Console.WriteLine($"{entry.Id}\t{entry.OriginalPath}\t{entry.TrashedAt:O}");
                if (!_quiet) Console.Error.WriteLine($"ゴミ箱: {entries.Count} 件");
                return 0;
            }
            if (positional.Count == 3 && positional[0] == "notes" && positional[1] == "trash")
            {
                var root = await ResolveInputAsync(input);
                var source = ResolveNotePath(root, positional[2]);
                var entry = new NoteFileService(root).MoveToTrash(source);
                Console.WriteLine(entry.Id);
                return 0;
            }
            if (positional.Count == 3 && positional[0] == "notes" && positional[1] == "restore")
            {
                var root = await ResolveInputAsync(input);
                var restored = new NoteFileService(root).Restore(positional[2]);
                Console.WriteLine(Path.GetRelativePath(root, restored));
                return 0;
            }
            if (positional.Count == 2 && positional[0] == "config" && positional[1] == "show")
            {
                var config = await ResolveConfigAsync(configPath);
                Console.WriteLine(JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (positional.Count == 1 && positional[0] == "doctor")
                return await RunDoctorAsync(configPath);
            if (positional.Count == 4 && positional[0] == "config" && positional[1] == "set")
            {
                var config = await ResolveConfigAsync(configPath);
                var key = positional[2];
                var value = positional[3];
                config = key switch
                {
                    "input" => config with { InputPath = value },
                    "output" => config with { OutputPath = value },
                    "builder" when value is "builtin" or "external" => config with { Builder = value },
                    "external.project" => config with { External = config.External with { ProjectPath = value } },
                    "external.executable" => config with { External = config.External with { Executable = value } },
                    "external.arguments" => config with { External = config.External with { Arguments = JsonSerializer.Deserialize<string[]>(value) ?? [] } },
                    "external.timeout" when int.TryParse(value, out var seconds) => config with { External = config.External with { TimeoutSeconds = seconds } },
                    _ => null!
                };
                if (config is null) return UsageError($"不明または不正な設定項目です: {key}");
                if (configPath is not null)
                {
                    var target = Path.GetFullPath(configPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await config.SaveAsync(target);
                }
                else await new WorkspaceSettings().SaveAsync(config);
                if (!_quiet) Console.WriteLine($"設定を更新しました: {key}");
                return 0;
            }
            if (positional.Count == 2 && positional[0] == "notes" && positional[1] == "list")
            {
                var root = await ResolveInputAsync(input);
                var notes = new NoteRepository().ReadAll(root, Path.Combine(root, ".tatami-no-output"))
                    .OrderBy(note => Path.GetRelativePath(root, note.SourcePath), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var note in notes)
                {
                    var relative = Path.GetRelativePath(root, note.SourcePath);
                    Console.WriteLine($"{(PublishFilter.IsPublic(note) ? "公開" : "非公開")}\t{relative}");
                    if (_verbose && note.Metadata.Title is { Length: > 0 } title) Console.Error.WriteLine($"  title: {title}");
                }
                if (!_quiet) Console.Error.WriteLine($"{notes.Length} 件のノート");
                return 0;
            }
            if (positional.Count == 3 && positional[0] == "notes" && positional[1] == "show")
            {
                var root = await ResolveInputAsync(input);
                var path = Path.GetFullPath(Path.Combine(root, positional[2]));
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (path == root || !path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                    return UsageError("ノートのパスは入力ディレクトリ内を指定してください。");
                if (!File.Exists(path) || !Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
                    return UsageError($"Markdownノートが見つかりません: {path}");
                var note = new NoteRepository().Parse(path, await File.ReadAllTextAsync(path));
                Console.WriteLine($"path: {Path.GetRelativePath(root, path)}");
                Console.WriteLine($"publish: {(PublishFilter.IsPublic(note) ? "true (公開)" : "false (非公開)")}");
                Console.WriteLine("front matter:");
                if (note.FrontMatter is null) Console.WriteLine("  (なし)");
                else Console.Write(note.FrontMatter);
                return 0;
            }
            return UsageError("未対応のコマンドです。`tatami --help` を参照してください。");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or InvalidDataException or ArgumentException)
        {
            if (json && positional.Count == 1 && positional[0] == "build")
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, published = (int?)null, privateCount = (int?)null, output, error = ex.Message }));
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            if (json && positional.Count == 1 && positional[0] == "build")
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, published = (int?)null, privateCount = (int?)null, output, error = ex.Message }));
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
    }

    private static int BuildUsageError(bool json, string output, string message)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { success = false, published = (int?)null, privateCount = (int?)null, output, error = message }));
        return UsageError(message);
    }

    private static async Task<SiteConfig> ResolveConfigAsync(string? configPath)
    {
        if (configPath is not null)
        {
            var fullPath = Path.GetFullPath(configPath);
            return File.Exists(fullPath) ? await SiteConfig.LoadAsync(fullPath) : new SiteConfig("", "");
        }
        return await new WorkspaceSettings().LoadAsync() ?? new SiteConfig("", "");
    }

    private static async Task<int> RunDoctorAsync(string? configPath)
    {
        var problems = 0;
        void Check(string label, bool ok, string detail)
        {
            Console.Error.WriteLine($"{(ok ? "OK" : "NG")}\t{label}\t{detail}");
            if (!ok) problems++;
        }

        var sdk = await RunVersionAsync("dotnet", "--version");
        Check(".NET SDK", sdk is not null, sdk ?? "dotnet --version を実行できません");

        SiteConfig? config = null;
        try
        {
            if (configPath is not null)
            {
                var fullPath = Path.GetFullPath(configPath);
                if (!File.Exists(fullPath))
                    Check("設定ファイル", false, $"見つかりません: {fullPath}");
                else
                {
                    config = await SiteConfig.LoadAsync(fullPath);
                    Check("設定ファイル", true, fullPath);
                }
            }
            else
            {
                config = await new WorkspaceSettings().LoadAsync();
                Check("ユーザー設定", config is not null,
                    config is null ? "未設定です。config set または --config を使ってください。" : "読み込みました。");
            }
        }
        catch (Exception ex) { Check("設定", false, ex.Message); }

        if (config is not null)
        {
            var input = ResolveConfiguredPath(config.InputPath);
            var output = ResolveConfiguredPath(config.OutputPath);
            Check("入力パス", input is not null && Directory.Exists(input), input ?? "未設定または存在しません");
            Check("出力パス", output is not null && IsOutputPathSafe(input, output), output ?? "未設定または入力と競合");
            Check("ビルダー設定", config.Builder is "builtin" or "external", config.Builder);
            if (config.Builder == "external")
            {
                var executable = config.External.Executable;
                var version = await RunVersionAsync(executable, "--version");
                Check("外部ビルダー実行ファイル", version is not null,
                    version is null ? $"起動または --version に失敗: {executable}" : $"{executable} ({version})");
                var project = ResolveConfiguredPath(config.External.ProjectPath);
                Check("外部ビルダープロジェクト", project is not null && Directory.Exists(project), project ?? "未設定または存在しません");
                Check("外部ビルドタイムアウト", config.External.TimeoutSeconds is >= 1 and <= 86400,
                    $"{config.External.TimeoutSeconds} 秒（許容範囲: 1〜86400秒）");
                if (project is not null)
                {
                    var inputSeparated = input is not null && !SamePath(input, project);
                    Check("入力と外部プロジェクト", inputSeparated,
                        inputSeparated ? "別々のフォルダです" : "ノートと外部プロジェクトに別のフォルダを指定してください");
                    var outputSeparated = output is not null && !IsWithin(project, output);
                    Check("出力先と外部プロジェクト", outputSeparated,
                        outputSeparated ? "ビルダーの出力先はプロジェクト外です" : "出力先を外部プロジェクトの外側にしてください");
                }
            }
        }

        return problems == 0 ? 0 : 1;
    }

    private static string? ResolveConfiguredPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static bool IsOutputPathSafe(string? input, string output)
    {
        if (input is null) return false;
        return Directory.Exists(input)
            && !IsWithin(input, output);
    }

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), comparison);
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.Equals(fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static async Task<string?> RunVersionAsync(string executable, string argument)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                ArgumentList = { argument },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return null;
            }
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) return null;
            return string.IsNullOrWhiteSpace(stdout.Result) ? stderr.Result.Trim() : stdout.Result.Trim();
        }
        catch { return null; }
        finally { process?.Dispose(); }
    }

    private static async Task<string> ResolveInputAsync(string? input)
    {
        if (!string.IsNullOrWhiteSpace(input))
        {
            var selected = Path.GetFullPath(input);
            if (!Directory.Exists(selected)) throw new DirectoryNotFoundException($"入力ディレクトリがありません: {selected}");
            return selected;
        }
        var config = await new WorkspaceSettings().LoadAsync();
        var configured = config?.InputPath;
        if (string.IsNullOrWhiteSpace(configured))
            throw new DirectoryNotFoundException("入力ディレクトリが未設定です。--input <dir> を指定してください。");
        var full = Path.GetFullPath(configured);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"設定された入力ディレクトリがありません: {full}");
        return full;
    }

    private static string ResolveNotePath(string root, string notePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, notePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("ノートのパスは入力ディレクトリ内を指定してください。");
        return fullPath;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"引数エラー: {message}");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
tatami {Version}
使い方:
  tatami [--quiet|--verbose] notes list [--input <dir>]
  tatami [--quiet|--verbose] notes show <path> [--input <dir>]
  tatami [--quiet|--verbose] notes create <name> [--directory <dir>] [--input <dir>]
  tatami [--quiet|--verbose] notes rename <path> <name> [--input <dir>]
  tatami [--quiet|--verbose] notes trash <path> [--input <dir>]
  tatami [--quiet|--verbose] notes trash list [--input <dir>]
  tatami [--quiet|--verbose] notes restore <id> [--input <dir>]
  tatami config show [--config <file>]
  tatami config set <key> <value> [--config <file>]
  tatami doctor [--config <file>]
  tatami [--quiet|--verbose] build [--input <dir>] [--output <dir>] [--config <file>] [--json]
  tatami --help
  tatami --version

notes list   Markdownノートを再帰的に一覧表示します（公開状態つき）。
notes show   Front Matterと公開判定を表示します。
notes create 非公開の新規ノートを作成します。
notes rename ファイル名を変更します（既存ファイルは上書きしません）。
notes trash  ノートをアプリ内ゴミ箱へ移動します。完全削除はしません。
notes restore ゴミ箱のノートを元の場所へ復元します。
config show   現在のビルド設定を表示します。
config set    input / output / builder / external.* を設定します。
doctor        .NET・設定・パス・外部ビルダーを診断します。
build        設定済みビルダーでサイトを生成します。
--input      対象ノートのディレクトリ（省略時は設定を使用）。
--output     HTML出力先（省略時は設定を使用）。
--config     設定JSONのパス（省略時はアプリ設定を使用）。
--json       build結果をJSONで標準出力へ出します。
--quiet      補助メッセージを抑制します。
--verbose    補足情報を標準エラーへ表示します。

終了コード: 0 成功、1 実行時エラー、2 引数エラー
""");
    }
}
