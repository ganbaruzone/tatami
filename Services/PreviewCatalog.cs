using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Tatami.Services;

public sealed record PreviewPage(string Name, string FullPath)
{
    public override string ToString() => Name;
}

public static class PreviewCatalog
{
    public static async Task<IReadOnlyList<PreviewPage>> ReadAsync(string output)
    {
        var manifest = Path.Combine(output, ".tatami-manifest.json");
        PathSafety.RejectLinks(manifest);
        if (!File.Exists(manifest)) return [];
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(manifest))
            ?? throw new InvalidDataException("生成物一覧を読み込めません。");
        return [.. entries.Keys.Where(key => key.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .Select(key => new PreviewPage(key == "index.html" ? "/" : key.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) ? key[..^11] : key, PathSafety.OutputFile(output, key)))
            .Where(page => File.Exists(page.FullPath)).OrderBy(page => page.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static bool CanNavigate(Uri uri, string output)
    {
        if (uri.ToString() == "about:blank") return true;
        if (!uri.IsFile || !PathSafety.IsWithin(uri.LocalPath, output)) return false;
        try { PathSafety.RejectLinks(uri.LocalPath); }
        catch (IOException) { return false; }
        return File.Exists(uri.LocalPath) && Path.GetExtension(uri.LocalPath).Equals(".html", StringComparison.OrdinalIgnoreCase);
    }
}
