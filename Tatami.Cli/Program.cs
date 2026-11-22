using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Tatami.Models;
using Tatami.Services;

return await TatamiCli.RunAsync(args);

internal static partial class TatamiCli
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

        if (!CliArguments.TryParse(args, out var parsed, out var parseError))
            return UsageError(parseError!);
        var cliArguments = parsed!;
        var positional = cliArguments.Positional;
        var positionalCount = positional.Count;
        var isBuildCommand = positionalCount == 1 && positional[0] == "build";
        var isPreviewCommand = positionalCount == 1 && positional[0] == "preview";
        var input = cliArguments.Input;
        var output = cliArguments.Output;
        var configPath = cliArguments.ConfigPath;
        var json = cliArguments.Json;
        var watch = cliArguments.Watch;
        _quiet = cliArguments.Quiet;
        _verbose = cliArguments.Verbose;

        try
        {
            var noteResult = await RunNotesCommandAsync(cliArguments);
            if (noteResult.HasValue) return noteResult.Value;

            var configResult = await RunConfigCommandAsync(cliArguments);
            if (configResult.HasValue) return configResult.Value;

            if (isBuildCommand)
            {
                if (watch && json) return UsageError("--watch と --json は同時に指定できません。");
                var config = await ResolveConfigAsync(configPath);
                var effective = config with
                {
                    InputPath = input is null ? config.InputPath : Path.GetFullPath(input),
                    OutputPath = output is null ? config.OutputPath : Path.GetFullPath(output)
                };
                if (string.IsNullOrWhiteSpace(effective.InputPath) || string.IsNullOrWhiteSpace(effective.OutputPath))
                    return BuildUsageError(json, effective.OutputPath, "入力・出力ディレクトリが必要です。--input と --output を指定するか、設定してください。");
                if (watch) return await RunWatchBuildAsync(effective);
                var progress = _quiet ? null : new Progress<string>(message => Console.Error.WriteLine(message));
                var result = await BuildCoordinator.BuildAsync(effective, progress);
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new { success = true, published = result.PublishedCount, privateCount = result.PrivateCount, output = result.OutputPath }));
                else if (!_quiet)
                    Console.WriteLine($"ビルド成功: 公開 {result.PublishedCount} 件、非公開 {result.PrivateCount} 件、出力先 {result.OutputPath}");
                return 0;
            }
            if (isPreviewCommand)
            {
                var config = await ResolveConfigAsync(configPath);
                var previewOutput = output is null ? config.OutputPath : Path.GetFullPath(output);
                if (string.IsNullOrWhiteSpace(previewOutput))
                    return UsageError("HTML出力ディレクトリが必要です。--output または --config を指定してください。");
                previewOutput = Path.GetFullPath(previewOutput);
                var pages = await PreviewCatalog.ReadAsync(previewOutput);
                if (pages.Count == 0)
                    throw new InvalidDataException($"プレビュー可能なHTMLがありません。先に tatami build を実行してください: {previewOutput}");

                await using var server = new PreviewServer();
                await server.StartAsync(previewOutput);
                var address = server.GetUri(pages[0].FullPath);
                using var stop = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    stop.Cancel();
                };
                Console.CancelKeyPress += cancel;
                try
                {
                    Console.WriteLine(address);
                    if (!_quiet) Console.Error.WriteLine("ローカルプレビューを起動しました。終了するには Ctrl+C を押してください。");
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
                finally { Console.CancelKeyPress -= cancel; }
                return 0;
            }
            return UsageError("未対応のコマンドです。`tatami --help` を参照してください。");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or InvalidDataException or ArgumentException)
        {
            if (json && isBuildCommand)
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, published = (int?)null, privateCount = (int?)null, output, error = ex.Message }));
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            if (json && isBuildCommand)
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

    private static async Task<int> RunWatchBuildAsync(SiteConfig config)
    {
        var inputRoot = RequireDirectory(config.InputPath, "入力ディレクトリがありません");
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
        using var watcher = new FileSystemWatcher(inputRoot, "*")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = false
        };
        void Changed(object? _, FileSystemEventArgs change)
        {
            var relative = Path.GetRelativePath(inputRoot, change.FullPath);
            if (!Path.GetExtension(change.FullPath).Equals(".md", StringComparison.OrdinalIgnoreCase)
                || relative.Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".tatami-trash", StringComparison.OrdinalIgnoreCase)
                    || part.Equals(".git", StringComparison.OrdinalIgnoreCase))) return;
            changes.Writer.TryWrite(true);
        }
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += (_, change) => { Changed(null, change); if (!string.IsNullOrEmpty(change.OldFullPath)) Changed(null, new FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(change.OldFullPath)!, Path.GetFileName(change.OldFullPath))); };
        watcher.Error += (_, _) => changes.Writer.TryWrite(true);
        watcher.EnableRaisingEvents = true;
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        var lastBuildFailed = false;
        try
        {
            if (!_quiet) Console.Error.WriteLine("Markdownノートを監視しています。終了するには Ctrl+C を押してください。");
            await RunWatchBuildOnceAsync(config, stop.Token);
            while (await changes.Reader.WaitToReadAsync(stop.Token))
            {
                while (changes.Reader.TryRead(out _)) { }
                await Task.Delay(TimeSpan.FromMilliseconds(600), stop.Token);
                while (changes.Reader.TryRead(out _)) { }
                if (!_quiet) Console.Error.WriteLine("ノートの変更を検出しました。再ビルドします。");
                try
                {
                    await RunWatchBuildOnceAsync(config, stop.Token);
                    lastBuildFailed = false;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"再ビルドに失敗しました: {ex.Message}");
                    lastBuildFailed = true;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= cancel; }
        if (!_quiet) Console.Error.WriteLine("監視を終了しました。");
        return lastBuildFailed ? 1 : 0;
    }

    private static async Task RunWatchBuildOnceAsync(SiteConfig config, CancellationToken cancellationToken)
    {
        var progress = _quiet ? null : new Progress<string>(message => Console.Error.WriteLine(message));
        var result = await BuildCoordinator.BuildAsync(config, progress, cancellationToken);
        if (!_quiet) Console.WriteLine($"ビルド成功: 公開 {result.PublishedCount} 件、非公開 {result.PrivateCount} 件、出力先 {result.OutputPath}");
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
            return RequireDirectory(input, "入力ディレクトリがありません");

        var config = await new WorkspaceSettings().LoadAsync();
        var configured = config?.InputPath;
        if (string.IsNullOrWhiteSpace(configured))
            throw new DirectoryNotFoundException("入力ディレクトリが未設定です。--input <dir> を指定してください。");
        return RequireDirectory(configured, "設定された入力ディレクトリがありません");
    }

    private static string RequireDirectory(string path, string errorMessage)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"{errorMessage}: {fullPath}");
        return fullPath;
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

    private static string GetFileSlug(string name) => GetSlug(Regex.Replace(name.Trim(), @"\s+", "-"));

    private static string GetSlug(string value)
    {
        value = value.Normalize(System.Text.NormalizationForm.FormC);
        if (!Regex.IsMatch(value, @"^[\p{L}\p{N}_-]+$", RegexOptions.CultureInvariant)
            || value.Length > 100
            || Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase))
            throw new InvalidDataException($"不正なslugです: {value}（文字・数字・ハイフン・アンダースコア、100文字以内）");
        return value;
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
  tatami [--quiet|--verbose] notes search <query> [--input <dir>]
  tatami [--quiet|--verbose] notes validate [--input <dir>]
  tatami [--quiet|--verbose] notes show <path> [--input <dir>]
  tatami [--quiet|--verbose] notes create <name> [--directory <dir>] [--input <dir>]
  tatami [--quiet|--verbose] notes rename <path> <name> [--input <dir>]
  tatami [--quiet|--verbose] notes metadata set <path> <title|publish|slug> <value> [--input <dir>]
  tatami [--quiet|--verbose] notes trash <path> [--input <dir>]
  tatami [--quiet|--verbose] notes trash list [--input <dir>]
  tatami [--quiet|--verbose] notes restore <id> [--input <dir>]
  tatami config show [--config <file>]
  tatami config set <key> <value> [--config <file>]
  tatami doctor [--config <file>]
  tatami [--quiet|--verbose] build [--input <dir>] [--output <dir>] [--config <file>] [--json]
  tatami [--quiet|--verbose] build --watch [--input <dir>] [--output <dir>] [--config <file>]
  tatami [--quiet|--verbose] preview [--output <dir>] [--config <file>]
  tatami --help
  tatami --version

notes list   Markdownノートを再帰的に一覧表示します（公開状態つき）。
notes search 本文・タイトル・タグから語句を検索し、相対パスと行番号を表示します。
notes validate Front Matter、公開slugの重複、非公開ブロックを検査します（出力先は変更しません）。
notes show   Front Matterと公開判定を表示します。
notes create 非公開の新規ノートを作成します。
notes rename ファイル名を変更します（既存ファイルは上書きしません）。
notes metadata set 公開状態・タイトル・slugを変更します。
notes trash  ノートをアプリ内ゴミ箱へ移動します。完全削除はしません。
notes restore ゴミ箱のノートを元の場所へ復元します。
config show   現在のビルド設定を表示します。
config set    input / output / builder / external.* を設定します。
doctor        .NET・設定・パス・外部ビルダーを診断します。
build        設定済みビルダーでサイトを生成します。
preview      生成済みHTMLを127.0.0.1で配信します（Ctrl+Cで終了）。
--input      対象ノートのディレクトリ（省略時は設定を使用）。
--output     HTML出力先（省略時は設定を使用）。
--config     設定JSONのパス（省略時はアプリ設定を使用）。
--json       build結果をJSONで標準出力へ出します。
--watch      Markdown変更後に再ビルドします（Ctrl+Cで終了）。
--quiet      補助メッセージを抑制します。
--verbose    補足情報を標準エラーへ表示します。

終了コード: 0 成功、1 実行時エラー、2 引数エラー
""");
    }
}
