using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tatami.Services;

internal static partial class PathSafety
{
    // Case-insensitive comparisons deliberately also protect portable sites on Windows/macOS.
    public static bool IsWithin(string path, string root) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Slug(string value)
    {
        value = value.Normalize(NormalizationForm.FormC);
        if (!MyRegex().IsMatch(value)
            || value.Length > 100 || Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase))
            throw new InvalidDataException($"不正なslugです: {value}（文字・数字・ハイフン・アンダースコア、100文字以内）");
        return value;
    }

    public static string FileSlug(string name) => Slug(Regex.Replace(name.Trim(), @"\s+", "-"));

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            var info = new FileInfo(current);
            if (info.LinkTarget != null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException($"シンボリックリンクを含むパスには出力できません: {current}");
        }
    }

    public static string OutputFile(string root, string relative)
    {
        var parts = relative.Split('/');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Any(char.IsControl)
            || parts.Any(part => part is "" or "." or ".." || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
                || part.EndsWith('.') || part.EndsWith(' ')
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith(".tatami-", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("生成物一覧に不正なパスがあります。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(path, root)) throw new InvalidDataException("出力先の範囲外です。");
        RejectLinks(path);
        return path;
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
