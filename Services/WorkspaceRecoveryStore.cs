using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tatami.Models;

namespace Tatami.Services;

public sealed class WorkspaceRecoveryStore
{
    private readonly string _directory;

    public WorkspaceRecoveryStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tatami", "recovery");

    private string GetPath(string workspace)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return Path.Combine(_directory, digest + ".json");
    }

    public async Task SaveAsync(WorkspaceRecoverySnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Tabs.Length == 0)
            throw new InvalidDataException("自動退避データの形式が不正です。");
        var path = GetPath(snapshot.WorkspacePath);
        Directory.CreateDirectory(_directory);
        if (File.Exists(path))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<WorkspaceRecoverySnapshot>(await File.ReadAllTextAsync(path));
                if (previous is not null && previous.Tabs is not null && previous.WorkspacePath == snapshot.WorkspacePath
                    && previous.ActiveTabIndex == snapshot.ActiveTabIndex
                    && previous.Tabs.SequenceEqual(snapshot.Tabs)) return;
            }
            catch (JsonException) { }
        }
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<WorkspaceRecoverySnapshot?> LoadAsync(string workspace)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var path = GetPath(root);
        if (!File.Exists(path)) return null;
        var snapshot = JsonSerializer.Deserialize<WorkspaceRecoverySnapshot>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("自動退避データを読み込めません。");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (snapshot.Version != 1 || !Path.GetFullPath(snapshot.WorkspacePath).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(root, comparison) || snapshot.Tabs is null || snapshot.Tabs.Length > 500)
            throw new InvalidDataException("自動退避データの対象または形式が一致しません。");

        var valid = new System.Collections.Generic.List<RecoveredNoteTab>();
        foreach (var tab in snapshot.Tabs)
        {
            if (tab is null) continue;
            var notePath = Path.GetFullPath(tab.Path);
            if (!PathSafety.IsWithin(notePath, root) || !Path.GetExtension(notePath).Equals(".md", StringComparison.OrdinalIgnoreCase)
                || Path.GetRelativePath(root, notePath).Split(Path.DirectorySeparatorChar)
                    .Any(part => part.Equals(NoteFileService.TrashFolderName, StringComparison.OrdinalIgnoreCase))
                || !File.Exists(notePath) || string.IsNullOrWhiteSpace(tab.BaselineHash) || tab.BaselineHash.Length != 64
                || !tab.BaselineHash.All(Uri.IsHexDigit))
                continue;
            PathSafety.RejectLinks(notePath);
            valid.Add(tab with { Path = notePath, CaretOffset = Math.Max(0, tab.CaretOffset) });
        }
        if (valid.Count == 0)
        {
            await DeleteAsync(root);
            return null;
        }
        return snapshot with
        {
            WorkspacePath = root,
            ActiveTabIndex = Math.Clamp(snapshot.ActiveTabIndex, 0, valid.Count - 1),
            Tabs = valid.ToArray()
        };
    }

    public Task DeleteAsync(string workspace)
    {
        var path = GetPath(workspace);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
