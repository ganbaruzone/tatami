using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tatami.Services;

public sealed record TrashedNote(string Id, string OriginalPath, DateTimeOffset TrashedAt)
{
    public override string ToString() => $"{OriginalPath}  —  {TrashedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
}

// An application trash keeps all operations portable and reversible, without permanent deletion.
public sealed partial class NoteFileService
{
    public const string TrashFolderName = ".tatami-trash";
    private readonly string _root;
    private string TrashRoot => Path.Combine(_root, TrashFolderName);

    public NoteFileService(string workspace)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (_root.Split(Path.DirectorySeparatorChar).Any(part => part.Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase)
            || part.Equals(AttachmentService.FolderName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Tatamiの内部データ領域をメモのディレクトリには指定できません。");
        PathSafety.RejectLinks(_root);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("メモのディレクトリがありません。");
    }

    public static string NormalizeName(string name)
    {
        name = name.Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.StartsWith('.')
            || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0
            || name.EndsWith('.') || name.Length > 120
            || MyRegex().IsMatch(name.Split('.')[0]))
            throw new InvalidDataException("ファイル名が不正です。区切り記号・予約名・先頭や末尾の空白は使えません（120文字以内）。");
        if (!Path.HasExtension(name)) name += ".md";
        if (!Path.GetExtension(name).Equals(".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Markdownファイル（.md）の名前を入力してください。");
        return name;
    }

    private string CheckPath(string path)
    {
        path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path != _root && !path.StartsWith(_root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("メモのディレクトリ外は操作できません。");
        if (Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)
            .Any(part => part.Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase)
                || part.Equals(AttachmentService.FolderName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Tatamiの内部データ領域は直接操作できません。");
        PathSafety.RejectLinks(path);
        return path;
    }

    private string CheckNote(string path)
    {
        path = CheckPath(path);
        NormalizeName(Path.GetFileName(path));
        if (!File.Exists(path)) throw new FileNotFoundException("ノートが見つかりません。", path);
        return path;
    }

    private static void EnsureAvailable(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        if (Directory.Exists(parent) && Directory.EnumerateFileSystemEntries(parent)
            .Any(entry => Path.GetFileName(entry).Normalize(NormalizationForm.FormC)
                .Equals(Path.GetFileName(path).Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("同名のファイルまたはフォルダが存在します。別の名前を指定してください。");
    }

    public string Create(string directory, string name, string? contents = null)
    {
        directory = CheckPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("作成先フォルダがありません。");
        var requestedName = NormalizeName(name);
        var stem = Path.GetFileNameWithoutExtension(requestedName);
        var extension = Path.GetExtension(requestedName);
        var existingNames = Directory.EnumerateFileSystemEntries(directory)
            .Select(entry => Path.GetFileName(entry).Normalize(NormalizationForm.FormC))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var suffix = 1; ; suffix++)
        {
            var candidateName = suffix == 1 ? requestedName : $"{stem}{suffix}{extension}";
            if (candidateName.Length > 120)
                throw new IOException("同名のファイルまたはフォルダが存在し、番号付きの名前を作成できません。");
            if (!existingNames.Add(candidateName.Normalize(NormalizationForm.FormC))) continue;

            var path = CheckPath(Path.Combine(directory, candidateName));
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                continue;
            }

            using (stream)
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(contents ?? "---\npublish: false\n---\n\n");
            return path;
        }
    }

    public string Rename(string source, string name)
    {
        source = CheckNote(source);
        var destination = CheckPath(Path.Combine(Path.GetDirectoryName(source)!, NormalizeName(name)));
        if (source == destination) return source;
        EnsureAvailable(destination);
        File.Move(source, destination, overwrite: false);
        return destination;
    }

    public string Move(string source, string directory)
    {
        source = CheckNote(source);
        directory = CheckPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("移動先フォルダがありません。");
        var destination = CheckPath(Path.Combine(directory, Path.GetFileName(source)));
        if (string.Equals(source, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return source;
        EnsureAvailable(destination);
        File.Move(source, destination, overwrite: false);
        return destination;
    }

    public TrashedNote MoveToTrash(string source)
    {
        source = CheckNote(source);
        PathSafety.RejectLinks(TrashRoot);
        GitRepositoryService.EnsureIgnored(_root, TrashRoot);
        var entry = new TrashedNote(Guid.NewGuid().ToString("D"), Path.GetRelativePath(_root, source), DateTimeOffset.UtcNow);
        var directory = Path.Combine(TrashRoot, entry.Id);
        Directory.CreateDirectory(directory);
        var metadata = Path.Combine(directory, "entry.json");
        File.WriteAllText(metadata, JsonSerializer.Serialize(entry));
        // If a move fails, the source remains untouched. Entries without a payload are not listed.
        File.Move(source, Path.Combine(directory, "note.md"), overwrite: false);
        return entry;
    }

    private TrashedNote ReadEntry(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidDataException("ゴミ箱の識別子が不正です。");
        var folder = Path.Combine(TrashRoot, id);
        var metadata = Path.Combine(folder, "entry.json");
        PathSafety.RejectLinks(metadata);
        PathSafety.RejectLinks(Path.Combine(folder, "note.md"));
        var entry = JsonSerializer.Deserialize<TrashedNote>(File.ReadAllText(metadata))
            ?? throw new InvalidDataException("ゴミ箱の情報が不正です。");
        if (entry.Id != id || string.IsNullOrWhiteSpace(entry.OriginalPath) || Path.IsPathRooted(entry.OriginalPath)
            || entry.OriginalPath.Replace('\\', '/').Split('/').Any(part => part is ".." or "." or ""))
            throw new InvalidDataException("ゴミ箱の復元先が不正です。");
        var destination = CheckPath(Path.Combine(_root, entry.OriginalPath));
        NormalizeName(Path.GetFileName(destination));
        return entry;
    }

    public IReadOnlyList<TrashedNote> ListTrash()
    {
        PathSafety.RejectLinks(TrashRoot);
        if (!Directory.Exists(TrashRoot)) return [];
        return [.. Directory.EnumerateDirectories(TrashRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "note.md")))
            .Select(directory => ReadEntry(Path.GetFileName(directory)))
            .OrderByDescending(entry => entry.TrashedAt)];
    }

    public string Restore(string id)
    {
        var entry = ReadEntry(id);
        var destination = CheckPath(Path.Combine(_root, entry.OriginalPath));
        EnsureAvailable(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var folder = Path.Combine(TrashRoot, id);
        File.Move(Path.Combine(folder, "note.md"), destination, overwrite: false);
        // Restored entries retain small metadata files; no payload means they are no longer in the trash list.
        return destination;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase, "ja-JP")]
    private static partial Regex MyRegex();
}
