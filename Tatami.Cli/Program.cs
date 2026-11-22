using System.Reflection;
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
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--quiet" or "--verbose") continue;
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is "--input" or "--output" or "--config")
            {
                var option = args[i];
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    return UsageError($"{option} には値を指定してください。");
                if (option == "--input") input = args[i];
                else if (option == "--output") output = args[i];
                else configPath = args[i];
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
                    return UsageError("入力・出力ディレクトリが必要です。--input と --output を指定するか、設定してください。");
                var progress = _quiet ? null : new Progress<string>(message => Console.Error.WriteLine(message));
                var result = await new BuildCoordinator().BuildAsync(effective, progress);
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new { success = true, published = result.PublishedCount, privateCount = result.PrivateCount, output = result.OutputPath }));
                else if (!_quiet)
                    Console.WriteLine($"ビルド成功: 公開 {result.PublishedCount} 件、非公開 {result.PrivateCount} 件、出力先 {result.OutputPath}");
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
    }

    private static async Task<SiteConfig> ResolveConfigAsync(string? configPath)
    {
        if (configPath is not null)
        {
            var fullPath = Path.GetFullPath(configPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException($"設定ファイルがありません: {fullPath}");
            return await SiteConfig.LoadAsync(fullPath);
        }
        return await new WorkspaceSettings().LoadAsync() ?? new SiteConfig("", "");
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
  tatami [--quiet|--verbose] build [--input <dir>] [--output <dir>] [--config <file>] [--json]
  tatami --help
  tatami --version

notes list   Markdownノートを再帰的に一覧表示します（公開状態つき）。
notes show   Front Matterと公開判定を表示します。
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
