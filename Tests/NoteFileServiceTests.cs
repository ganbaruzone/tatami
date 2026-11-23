using System.Text.Json;
using Tatami.Models;
using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class NoteFileServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _notes;
    private readonly NoteFileService _files;
    public NoteFileServiceTests()
    {
        var temp = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/")) temp = "/private" + temp;
        _root = Path.Combine(temp, "tatami-notes-" + Guid.NewGuid());
        _notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(_notes);
        _files = new NoteFileService(_notes);
    }

    [Fact]
    public void CreatesPrivateMarkdownAndAddsNumberWhenNameExists()
    {
        var path = _files.Create(_notes, "日本語 note");
        Assert.Equal("日本語 note.md", Path.GetFileName(path));
        Assert.False(new NoteRepository().Parse(path, File.ReadAllText(path)).Metadata.Publish);
        File.WriteAllText(path, "original");

        var second = _files.Create(_notes, "日本語 note.md");
        var third = _files.Create(_notes, "日本語 note.md");

        Assert.Equal("日本語 note2.md", Path.GetFileName(second));
        Assert.Equal("日本語 note3.md", Path.GetFileName(third));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.False(new NoteRepository().Parse(second, File.ReadAllText(second)).Metadata.Publish);
        Assert.False(new NoteRepository().Parse(third, File.ReadAllText(third)).Metadata.Publish);
    }

    [Fact]
    public void MovesNoteWithinWorkspaceAndRejectsOutsideOrCollision()
    {
        var source = _files.Create(_notes, "guide.md");
        var destinationFolder = Path.Combine(_notes, "folder");
        Directory.CreateDirectory(destinationFolder);
        var moved = _files.Move(source, destinationFolder);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(moved));
        Assert.Throws<InvalidDataException>(() => _files.Move(moved, _root));
        File.WriteAllText(Path.Combine(_notes, "guide.md"), "collision");
        Assert.Throws<IOException>(() => _files.Move(moved, _notes));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute.md")]
    [InlineData("folder/note.md")]
    [InlineData("folder\\note.md")]
    [InlineData("CON.md")]
    [InlineData(".hidden.md")]
    [InlineData("trailing .")]
    [InlineData("script.txt")]
    [InlineData("")]
    public void RejectsUnsafeNames(string name)
    {
        Assert.Throws<InvalidDataException>(() => _files.Create(_notes, name));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_notes));
    }

    [Fact]
    public async Task RenameKeepsUnsavedBufferAndSaveUsesNewPath()
    {
        var oldPath = _files.Create(_notes, "before");
        var buffer = await EditorBuffer.OpenAsync(oldPath);
        buffer.Text = "# unsaved changes";
        buffer.Rename(_notes, "after");
        Assert.False(File.Exists(oldPath));
        Assert.True(buffer.IsDirty);
        Assert.Equal("# unsaved changes", buffer.Text);
        Assert.Contains("publish: false", File.ReadAllText(buffer.Path));
        await buffer.SaveAsync();
        Assert.Equal("# unsaved changes", File.ReadAllText(Path.Combine(_notes, "after.md")));
        Assert.False(buffer.IsDirty);
        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public void RenameCollisionAndCaseCollisionLeaveBothFilesIntact()
    {
        var a = _files.Create(_notes, "a");
        var b = _files.Create(_notes, "b");
        File.WriteAllText(a, "A"); File.WriteAllText(b, "B");
        Assert.Throws<IOException>(() => _files.Rename(a, "b"));
        Assert.Throws<IOException>(() => _files.Rename(a, "B"));
        Assert.Equal("A", File.ReadAllText(a)); Assert.Equal("B", File.ReadAllText(b));
    }

    [Fact]
    public async Task TrashIsExcludedFromExplorerAndBuildAndCanBeRestored()
    {
        var nested = Path.Combine(_notes, "folder");
        Directory.CreateDirectory(nested);
        var path = _files.Create(nested, "published");
        var text = "---\npublish: true\n---\nPUBLIC NOTE";
        await File.WriteAllTextAsync(path, text);
        var output = Path.Combine(_root, "site");
        await new SiteBuilder().BuildAsync(_notes, output);
        var entry = _files.MoveToTrash(path);
        Assert.False(File.Exists(path));
        Assert.Equal(entry, Assert.Single(_files.ListTrash()));
        Assert.DoesNotContain(WorkspaceEntry.Read(_notes), item => item.Name == NoteFileService.TrashFolderName);
        Assert.Empty(new NoteRepository().ReadAll(_notes, output));
        await new SiteBuilder().BuildAsync(_notes, output);
        Assert.False(File.Exists(Path.Combine(output, "published/index.html")));
        Assert.Equal(path, _files.Restore(entry.Id));
        Assert.Equal(text, await File.ReadAllTextAsync(path));
        Assert.Empty(_files.ListTrash());
    }

    [Fact]
    public void RestoreCollisionPreservesTrashAndExistingFile()
    {
        var path = _files.Create(_notes, "note");
        File.WriteAllText(path, "trashed");
        var entry = _files.MoveToTrash(path);
        File.WriteAllText(path, "existing");
        Assert.Throws<IOException>(() => _files.Restore(entry.Id));
        Assert.Equal("existing", File.ReadAllText(path));
        Assert.Single(_files.ListTrash());
    }

    [Fact]
    public void RejectsSymlinksAndOutsideWorkspace()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Assert.Throws<InvalidDataException>(() => _files.Create(outside, "note"));
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_notes, "linked"), outside);
        Assert.Throws<IOException>(() => _files.Create(Path.Combine(_notes, "linked"), "note"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void TamperedTrashMetadataCannotRestoreOutsideWorkspace()
    {
        var path = _files.Create(_notes, "note");
        var entry = _files.MoveToTrash(path);
        var directory = Path.Combine(_notes, NoteFileService.TrashFolderName, entry.Id);
        File.WriteAllText(Path.Combine(directory, "entry.json"), JsonSerializer.Serialize(entry with { OriginalPath = "../escape.md" }));
        Assert.Throws<InvalidDataException>(() => _files.Restore(entry.Id));
        Assert.True(File.Exists(Path.Combine(directory, "note.md")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.md")));
    }

    [Fact]
    public void TrashCannotBeSelectedAsWorkspaceOrBuildInput()
    {
        var trash = Path.Combine(_notes, NoteFileService.TrashFolderName);
        Directory.CreateDirectory(trash);
        Assert.Throws<InvalidDataException>(() => new NoteFileService(trash));
        Assert.Throws<InvalidDataException>(() => new NoteRepository().ReadAll(trash, Path.Combine(_root, "site")).ToArray());
    }

    public void Dispose() => Directory.Delete(_root, true);
}
