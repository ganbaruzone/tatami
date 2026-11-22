using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Tatami.Models;

// Optional JSON configuration for callers that want to persist build paths.
public sealed record SiteConfig(string InputPath, string OutputPath)
{
    public string Builder { get; init; } = "builtin";
    public ExternalBuildOptions External { get; init; } = new();

    public static async Task<SiteConfig> LoadAsync(string path) =>
        JsonSerializer.Deserialize<SiteConfig>(await File.ReadAllTextAsync(path))
        ?? throw new InvalidDataException("サイト設定が空です。");

    public Task SaveAsync(string path) => File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public sealed record ExternalBuildOptions
{
    public string ProjectPath { get; init; } = "";
    public string Executable { get; init; } = "node";
    public string[] Arguments { get; init; } = ["--run", "build"];
    public int TimeoutSeconds { get; init; } = 600;
}
