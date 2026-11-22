using System.Text;
using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class EditorBufferTests : IDisposable
{
    private readonly string _folder;
    private readonly string _path;
    public EditorBufferTests()
    {
        var temp = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/")) temp = "/private" + temp;
        _folder = Path.Combine(temp, "tatami-editor-" + Guid.NewGuid());
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "note.md");
    }

    [Fact]
    public async Task EditsSaveAndUndoMarksClean()
    {
        await File.WriteAllTextAsync(_path, "# Original\n");
        var buffer = await EditorBuffer.OpenAsync(_path);
        Assert.False(buffer.IsDirty);
        buffer.Text = "# Edited\n";
        Assert.True(buffer.IsDirty);
        buffer.Text = "# Original\n";
        Assert.False(buffer.IsDirty);
        buffer.Text = "# 編集\n";
        await buffer.SaveAsync();
        Assert.False(buffer.IsDirty);
        Assert.Equal(buffer.Text, await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task ExternalChangesAreNotOverwritten()
    {
        await File.WriteAllTextAsync(_path, "Original");
        var buffer = await EditorBuffer.OpenAsync(_path);
        buffer.Text = "Buffer edits";
        await File.WriteAllTextAsync(_path, "External edits");
        await Assert.ThrowsAsync<IOException>(buffer.SaveAsync);
        Assert.Equal("External edits", await File.ReadAllTextAsync(_path));
        Assert.True(buffer.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesUtf8BomAndLineEndings(bool bom)
    {
        var encoding = new UTF8Encoding(bom);
        await File.WriteAllTextAsync(_path, "# 日本語\r\n", encoding);
        var buffer = await EditorBuffer.OpenAsync(_path);
        buffer.Text += "追記\r\n";
        await buffer.SaveAsync();
        var bytes = await File.ReadAllBytesAsync(_path);
        Assert.Equal(bom, bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("# 日本語\r\n追記\r\n", await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task DeletedFileIsNotRecreatedSilently()
    {
        await File.WriteAllTextAsync(_path, "Original");
        var buffer = await EditorBuffer.OpenAsync(_path);
        buffer.Text = "Changed";
        File.Delete(_path);
        await Assert.ThrowsAsync<FileNotFoundException>(buffer.SaveAsync);
        Assert.True(buffer.IsDirty);
        Assert.False(File.Exists(_path));
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
