using Tatami.Models;
using Tatami.Services;

internal static partial class TatamiCli
{
    private static async Task<int?> RunNotesCommandAsync(CliArguments arguments)
    {
        if (arguments.Positional.Count == 3 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "create")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var targetDirectory = arguments.Directory is null
                ? root
                : Path.GetFullPath(Path.Combine(root, arguments.Directory));
            var created = new NoteFileService(root).Create(targetDirectory, arguments.Positional[2]);
            Console.WriteLine(Path.GetRelativePath(root, created));
            return 0;
        }
        if (arguments.Positional.Count == 4 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "rename")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var source = ResolveNotePath(root, arguments.Positional[2]);
            var renamed = new NoteFileService(root).Rename(source, arguments.Positional[3]);
            Console.WriteLine(Path.GetRelativePath(root, renamed));
            return 0;
        }
        if (arguments.Positional.Count == 6 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "metadata" && arguments.Positional[2] == "set")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var path = ResolveNotePath(root, arguments.Positional[3]);
            if (!File.Exists(path) || !Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
                return UsageError($"Markdownノートが見つかりません: {path}");
            var current = await EditorBuffer.OpenAsync(path);
            var note = new NoteRepository().Parse(path, current.Text);
            var key = arguments.Positional[4];
            var value = arguments.Positional[5];
            string updated;
            switch (key)
            {
                case "title":
                    updated = NoteMetadataEditor.Apply(current.Text, value, note.Metadata.Publish, note.Metadata.Slug);
                    break;
                case "publish" when bool.TryParse(value, out var publish):
                    updated = NoteMetadataEditor.Apply(current.Text, note.Metadata.Title, publish, note.Metadata.Slug);
                    break;
                case "slug":
                    updated = NoteMetadataEditor.Apply(current.Text, note.Metadata.Title, note.Metadata.Publish, value);
                    break;
                default:
                    return UsageError("metadata key は title / publish / slug、publish value は true / false を指定してください。");
            }
            current.Text = updated;
            await current.SaveAsync();
            Console.WriteLine(Path.GetRelativePath(root, path));
            return 0;
        }
        if (arguments.Positional.Count == 3 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "trash" && arguments.Positional[2] == "list")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var entries = new NoteFileService(root).ListTrash();
            foreach (var entry in entries)
                Console.WriteLine($"{entry.Id}\t{entry.OriginalPath}\t{entry.TrashedAt:O}");
            if (!_quiet) Console.Error.WriteLine($"ゴミ箱: {entries.Count} 件");
            return 0;
        }
        if (arguments.Positional.Count == 3 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "trash")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var source = ResolveNotePath(root, arguments.Positional[2]);
            var entry = new NoteFileService(root).MoveToTrash(source);
            Console.WriteLine(entry.Id);
            return 0;
        }
        if (arguments.Positional.Count == 3 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "restore")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var restored = new NoteFileService(root).Restore(arguments.Positional[2]);
            Console.WriteLine(Path.GetRelativePath(root, restored));
            return 0;
        }
        if (arguments.Positional.Count == 2 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "list")
        {
            var root = await ResolveInputAsync(arguments.Input);
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
        if (arguments.Positional.Count == 2 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "validate")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var errors = new List<string>();
            var slugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Note[] notes;
            try { notes = new NoteRepository().ReadAll(root, Path.Combine(root, ".tatami-no-output")).ToArray(); }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"NG\t{ex.Message}");
                return 1;
            }
            foreach (var note in notes)
            {
                if (!PublishFilter.IsPublic(note)) continue;
                var relative = Path.GetRelativePath(root, note.SourcePath);
                try
                {
                    var slug = note.Metadata.Slug is null
                        ? GetFileSlug(Path.GetFileNameWithoutExtension(note.SourcePath))
                        : GetSlug(note.Metadata.Slug);
                    if (slugs.TryGetValue(slug, out var previous))
                        errors.Add($"{relative}: slug '{slug}' が {previous} と重複しています。");
                    else slugs.Add(slug, relative);
                    _ = PrivateContentFilter.Remove(note.Markdown);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                { errors.Add($"{relative}: {ex.Message}"); }
            }
            if (errors.Count > 0)
            {
                foreach (var error in errors) Console.Error.WriteLine($"NG\t{error}");
                Console.Error.WriteLine($"検査失敗: {errors.Count} 件");
                return 1;
            }
            Console.WriteLine($"検査成功: {notes.Length} 件（公開 {slugs.Count} 件）");
            return 0;
        }
        if (arguments.Positional.Count == 3 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "show")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var path = Path.GetFullPath(Path.Combine(root, arguments.Positional[2]));
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
        if (arguments.Positional.Count == 3 && arguments.Positional[0] == "notes" && arguments.Positional[1] == "search")
        {
            var root = await ResolveInputAsync(arguments.Input);
            var query = arguments.Positional[2];
            if (string.IsNullOrWhiteSpace(query)) return UsageError("検索語を指定してください。");
            var notes = new NoteRepository().ReadAll(root, Path.Combine(root, ".tatami-no-output"));
            var matches = 0;
            foreach (var note in notes)
            {
                var relative = Path.GetRelativePath(root, note.SourcePath);
                var metadataLine = note.FrontMatter is null ? 1 : 2;
                if (Path.GetFileNameWithoutExtension(note.SourcePath).Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"{relative}: ファイル名一致");
                    matches++;
                }
                if (note.Metadata.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                {
                    Console.WriteLine($"{relative}:{metadataLine}: title: {note.Metadata.Title}");
                    matches++;
                }
                foreach (var tag in note.Metadata.Tags ?? [])
                {
                    if (!tag.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine($"{relative}:{metadataLine}: tag: {tag}");
                    matches++;
                }
                var bodyLineOffset = note.FrontMatter?.Count(character => character == '\n') + (note.FrontMatter is null ? 0 : 2) ?? 0;
                var lines = note.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (var line = 0; line < lines.Length; line++)
                {
                    if (!lines[line].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine($"{relative}:{bodyLineOffset + line + 1}: {lines[line].Trim()}");
                    matches++;
                }
            }
            if (matches == 0) Console.WriteLine("一致するノートはありません。");
            else if (!_quiet) Console.Error.WriteLine($"{matches} 件の一致");
            return 0;
        }
        return null;
    }
}
