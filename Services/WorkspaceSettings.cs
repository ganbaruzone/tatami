using System;
using System.IO;
using System.Threading.Tasks;
using Tatami.Models;

namespace Tatami.Services;

public sealed class WorkspaceSettings(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tatami", "workspace.json");

    public async Task<SiteConfig?> LoadAsync() => File.Exists(_path) ? await SiteConfig.LoadAsync(_path) : null;

    public Task SaveAsync(string? input, string? output) => SaveAsync(new SiteConfig(input ?? "", output ?? ""));

    public async Task SaveAsync(SiteConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await config.SaveAsync(temporary);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
