using Xunit;

namespace Tatami.Tests;

[CollectionDefinition("CLI console", DisableParallelization = true)]
public sealed class CliConsoleCollection;

[Collection("CLI console")]
public sealed class NotesCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tatami-cli-" + Guid.NewGuid().ToString("N"));

    public NotesCommandsTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData()]
    [InlineData("notes")]
    [InlineData("other", "list")]
    [InlineData("notes", "unknown")]
    [InlineData("notes", "create")]
    [InlineData("notes", "create", "name", "extra")]
    [InlineData("notes", "rename", "name")]
    [InlineData("notes", "rename", "name", "new", "extra")]
    [InlineData("notes", "metadata", "get", "note.md", "title", "value")]
    [InlineData("notes", "metadata", "set", "note.md", "title")]
    [InlineData("notes", "metadata", "set", "note.md", "title", "value", "extra")]
    [InlineData("notes", "trash")]
    [InlineData("notes", "trash", "list", "extra")]
    [InlineData("notes", "restore")]
    [InlineData("notes", "restore", "id", "extra")]
    [InlineData("notes", "show")]
    [InlineData("notes", "show", "note.md", "extra")]
    [InlineData("notes", "search")]
    [InlineData("notes", "search", "query", "extra")]
    [InlineData("notes", "list", "extra")]
    [InlineData("notes", "validate", "extra")]
    public async Task UnsupportedCommandsDoNotResolveInput(params string[] command)
    {
        var (code, output, error) = await RunAsync([.. command, "--input", Path.Combine(_root, "missing")]);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Equal("引数エラー: 未対応のコマンドです。`tatami --help` を参照してください。" + Environment.NewLine, error);
    }

    [Fact]
    public async Task CreateMetadataRenameTrashAndRestorePreserveNotesAndLinks()
    {
        var created = await NotesAsync("create", "first");
        Assert.Equal((0, "first.md" + Environment.NewLine, ""), created);
        Assert.Contains("publish: false", await File.ReadAllTextAsync(Path.Combine(_root, "first.md")));
        await File.WriteAllTextAsync(Path.Combine(_root, "source.md"), "[link](first.md)\n");

        foreach (var (key, value) in new[] { ("title", "New title"), ("publish", "true"), ("slug", "new-slug") })
            Assert.Equal(0, (await NotesAsync("metadata", "set", "first.md", key, value)).Code);
        var text = await File.ReadAllTextAsync(Path.Combine(_root, "first.md"));
        var (code, _, error) = await NotesAsync("metadata", "set", "first.md", "publish", "invalid");
        Assert.Equal(2, code);
        Assert.Contains("metadata key", error);
        Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(_root, "first.md")));

        var renamed = await NotesAsync("rename", "first.md", "renamed");
        Assert.Equal((0, "renamed.md" + Environment.NewLine, ""), renamed);
        Assert.False(File.Exists(Path.Combine(_root, "first.md")));
        Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(_root, "renamed.md")));
        Assert.Contains("(renamed.md)", await File.ReadAllTextAsync(Path.Combine(_root, "source.md")));

        var (trashedCode, trashedOutput, _) = await NotesAsync("trash", "renamed.md");
        Assert.Equal(0, trashedCode);
        Assert.False(File.Exists(Path.Combine(_root, "renamed.md")));
        var (trashListCode, trashListOutput, trashListError) = await NotesAsync("trash", "list");
        Assert.Equal(0, trashListCode);
        Assert.Contains(trashedOutput.Trim() + "\trenamed.md\t", trashListOutput);
        Assert.Contains("ゴミ箱: 1 件", trashListError);
        Assert.Equal((0, "renamed.md" + Environment.NewLine, ""), await NotesAsync("restore", trashedOutput.Trim()));
        Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(_root, "renamed.md")));
    }

    [Fact]
    public async Task ListShowSearchAndValidatePreserveOutputAndDiagnostics()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "public.md"), "---\ntitle: Needle\npublish: true\nslug: public\ntags: [Needle]\n---\nNeedle body\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "private.md"), "private body\n");
        var (code, output, error) = await NotesAsync("list", "--verbose");
        Assert.Equal(0, code);
        Assert.Equal("非公開\tprivate.md" + Environment.NewLine + "公開\tpublic.md" + Environment.NewLine, output);
        Assert.Contains("title: Needle", error);
        Assert.Contains("2 件のノート", error);
        Assert.Equal("", (await NotesAsync("list", "--quiet")).Error);

        var (shownCode, shownOutput, _) = await NotesAsync("show", "public.md");
        Assert.Equal(0, shownCode);
        Assert.Contains("publish: true (公開)", shownOutput);
        Assert.Contains("title: Needle", shownOutput);
        var (searchCode, searchOutput, searchError) = await NotesAsync("search", "needle");
        Assert.Equal(0, searchCode);
        Assert.Equal("public.md:2: title: Needle" + Environment.NewLine + "public.md:2: tag: Needle" + Environment.NewLine + "public.md:7: Needle body" + Environment.NewLine, searchOutput);
        Assert.Contains("3 件の一致", searchError);
        Assert.Equal("一致するノートはありません。" + Environment.NewLine, (await NotesAsync("search", "absent")).Output);
        Assert.Equal(2, (await NotesAsync("search", " ")).Code);
        Assert.Equal((0, "検査成功: 2 件（公開 1 件）" + Environment.NewLine, ""), await NotesAsync("validate"));

        await File.WriteAllTextAsync(Path.Combine(_root, "duplicate.md"), "---\npublish: true\nslug: public\n---\n");
        var (validationCode, validationOutput, validationError) = await NotesAsync("validate");
        Assert.Equal(1, validationCode);
        Assert.Equal("", validationOutput);
        Assert.Contains("slug 'public'", validationError);
        Assert.Contains("検査失敗: 1 件", validationError);
    }

    [Theory]
    [InlineData("missing.md")]
    [InlineData("plain.txt")]
    public async Task ShowAndMetadataRejectNonNotes(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "plain.txt"), "unchanged");
        string[][] commands = [["show", name], ["metadata", "set", name, "title", "new"]];
        foreach (var command in commands)
        {
            var (code, output, error) = await NotesAsync(command);
            Assert.Equal(2, code);
            Assert.Equal("", output);
            Assert.Contains("引数エラー: Markdownノートが見つかりません:", error);
        }
        Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(_root, "plain.txt")));
    }

    private Task<(int Code, string Output, string Error)> NotesAsync(params string[] command) =>
        RunAsync(["notes", .. command, "--input", _root]);

    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var code = await TatamiCli.RunAsync(args);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
