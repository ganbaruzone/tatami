using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tatami.Models;

namespace Tatami.Services;

public sealed class SavedNoteViewService
{
    private readonly string _storagePath;

    public SavedNoteViewService(string workspacePath, string? storagePath = null)
    {
        if (storagePath != null)
        {
            _storagePath = storagePath;
            return;
        }

        var workspace = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace))).ToLowerInvariant();
        _storagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tatami", "SavedViews", key + ".json");
    }

    public IReadOnlyList<SavedNoteView> Load()
    {
        if (!File.Exists(_storagePath)) return [];
        return JsonSerializer.Deserialize<List<SavedNoteView>>(File.ReadAllText(_storagePath)) ?? [];
    }

    public void Save(SavedNoteView view)
    {
        var views = Load().ToList();
        var index = views.FindIndex(item => string.Equals(item.Name, view.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) views[index] = view;
        else views.Add(view);
        Write(views);
    }

    public IReadOnlyList<Note> Apply(IEnumerable<Note> notes, SavedNoteView view, string workspacePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var filtered = notes.Where(note =>
        {
            if (!string.IsNullOrWhiteSpace(view.Tag) && !note.Metadata.Tags.Contains(view.Tag, StringComparer.OrdinalIgnoreCase)) return false;
            if (view.PublishState == "public" && !note.Metadata.Publish) return false;
            if (view.PublishState == "private" && note.Metadata.Publish) return false;
            if (view.FromDate is DateOnly from && (note.Metadata.Date is not DateOnly fromDate || fromDate < from)) return false;
            if (view.ThroughDate is DateOnly through && (note.Metadata.Date is not DateOnly throughDate || throughDate > through)) return false;
            if (!string.IsNullOrWhiteSpace(view.Folder))
            {
                var folder = view.Folder.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
                var relativeFolder = Path.GetDirectoryName(Path.GetRelativePath(root, note.SourcePath)) ?? "";
                if (folder == "" || !(relativeFolder.Equals(folder, PathComparison) || relativeFolder.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison))) return false;
            }
            return true;
        });

        return view.Descending
            ? filtered.OrderByDescending(note => SortKey(note, view.SortBy, root), StringComparer.OrdinalIgnoreCase).ToArray()
            : filtered.OrderBy(note => SortKey(note, view.SortBy, root), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string SortKey(Note note, string sortBy, string root) => sortBy.ToLowerInvariant() switch
    {
        "title" => note.Metadata.Title ?? Path.GetFileNameWithoutExtension(note.SourcePath),
        "path" => Path.GetRelativePath(root, note.SourcePath),
        _ => note.Metadata.Date?.ToString("yyyy-MM-dd") ?? ""
    };

    private void Write(List<SavedNoteView> views)
    {
        var directory = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_storagePath, JsonSerializer.Serialize(views, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
