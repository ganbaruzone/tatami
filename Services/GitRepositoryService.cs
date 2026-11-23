using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Tatami.Services;

internal static class GitRepositoryService
{
    public static void EnsureIgnored(string workspace, string path)
    {
        var repository = RunGit(workspace, "rev-parse", "--show-toplevel");
        if (repository.ExitCode != 0) return;

        var root = Path.GetFullPath(repository.StandardOutput.Trim());
        var relativePath = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (Path.IsPathRooted(relativePath) || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;

        var gitPath = RunGit(root, "rev-parse", "--git-path", "info/exclude");
        if (gitPath.ExitCode != 0)
            throw new IOException($"Gitの除外設定を確認できません: {gitPath.StandardError.Trim()}");

        var excludePath = gitPath.StandardOutput.Trim();
        if (!Path.IsPathRooted(excludePath)) excludePath = Path.Combine(root, excludePath);
        excludePath = Path.GetFullPath(excludePath);
        PathSafety.RejectLinks(excludePath);

        var pattern = "/" + EscapeIgnorePattern(relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')) + "/";
        var contents = File.Exists(excludePath) ? File.ReadAllText(excludePath) : "";
        var existing = contents.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (existing.Contains(pattern, StringComparer.Ordinal)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
        using var stream = new FileStream(excludePath, FileMode.Append, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        if (contents.Length > 0 && !contents.EndsWith('\n')) writer.WriteLine();
        writer.WriteLine(pattern);
    }

    private static string EscapeIgnorePattern(string path) => string.Concat(path.Select(character =>
        character is '\\' or '*' or '?' or '[' or ']' ? "\\" + character : character.ToString()));

    private static GitResult RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process == null) return new GitResult(-1, "", "Gitを起動できません。");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new GitResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new GitResult(-1, "", "Gitがインストールされていません。");
        }
    }

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}
