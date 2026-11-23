using Tatami.Models;
using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class AttachmentServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _notes;
    private readonly string _source;
    private readonly AttachmentService _attachments = new();

    public AttachmentServiceTests()
    {
        var temp = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/", StringComparison.Ordinal)) temp = "/private" + temp;
        _root = Path.Combine(temp, "tatami-attachments-" + Guid.NewGuid().ToString("N"));
        _notes = Path.Combine(_root, "notes");
        _source = Path.Combine(_root, "source image.png");
        Directory.CreateDirectory(_notes);
        File.WriteAllBytes(_source, [137, 80, 78, 71]);
        File.WriteAllText(Path.Combine(_notes, "note.md"), "# Note\n");
    }

    [Fact]
    public void ImportsAllowedFileIntoManagedFolderAndCreatesRelativeMarkdown()
    {
        var result = _attachments.Import(_notes, Path.Combine(_notes, "note.md"), _source);

        Assert.True(File.Exists(result.Path));
        Assert.True(AttachmentService.IsManagedPath(_notes, result.Path));
        Assert.True(result.IsImage);
        Assert.StartsWith("![source image.png](<.tatami-assets/", result.Markdown);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, File.ReadAllBytes(result.Path));
    }

    [Fact]
    public void RejectsUnsupportedTypesAndSymlinkedAssetDirectory()
    {
        var unsupported = Path.Combine(_root, "payload.exe");
        File.WriteAllText(unsupported, "no");
        Assert.Throws<InvalidDataException>(() => _attachments.Import(_notes, Path.Combine(_notes, "note.md"), unsupported));
        Assert.False(Directory.Exists(Path.Combine(_notes, AttachmentService.FolderName)));

        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_notes, AttachmentService.FolderName), outside);
        Assert.Throws<IOException>(() => _attachments.Import(_notes, Path.Combine(_notes, "note.md"), _source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void HidesManagedAssetsFromNotesAndNormalFileOperations()
    {
        var managed = Path.Combine(_notes, AttachmentService.FolderName);
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "stray.md"), "not a note");

        Assert.DoesNotContain(WorkspaceEntry.Read(_notes), entry => entry.Name == AttachmentService.FolderName);
        Assert.DoesNotContain(new NoteRepository().ReadAll(_notes, Path.Combine(_root, "site")), note => note.SourcePath.Contains(AttachmentService.FolderName, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => new NoteFileService(managed));
        Assert.Throws<InvalidDataException>(() => new NoteFileService(_notes).Create(managed, "note.md"));
    }

    [Fact]
    public void ResolvesOnlyReferencedManagedAttachments()
    {
        var imported = _attachments.Import(_notes, Path.Combine(_notes, "note.md"), _source);
        var markdown = $"![attached](<{imported.RelativePath}>)\n\n::: private\n![private](<{imported.RelativePath}>)\n:::\n";
        var publicMarkdown = PrivateContentFilter.Remove(markdown);

        var found = _attachments.FindReferenced(_notes, Path.Combine(_notes, "note.md"), publicMarkdown);

        var asset = Assert.Single(found);
        Assert.Equal(imported.Path, asset.Path);
        Assert.StartsWith("assets/", asset.OutputPath);
    }

    [Fact]
    public void LinkCheckerReportsMissingManagedImageFiles()
    {
        var notePath = Path.Combine(_notes, "note.md");
        File.WriteAllText(notePath, "![missing](.tatami-assets/missing.png)\n");

        var broken = new NoteLinkService().CheckLinks(_notes);

        var result = Assert.Single(broken);
        Assert.Equal(".tatami-assets/missing.png", result.Url);
        Assert.Equal("添付ファイルがありません", result.Reason);
    }

    [Fact]
    public void RewritesImportedAttachmentLinksForGeneratedPageRoutes()
    {
        var imported = _attachments.Import(_notes, Path.Combine(_notes, "note.md"), _source);
        var routes = new Dictionary<string, string> { [imported.Path] = "assets/" + imported.FileName };
        var markdown = $"![image](<{imported.RelativePath}>)\n";

        var rewritten = _attachments.RewriteManagedLinksForOutput(_notes, Path.Combine(_notes, "note.md"), markdown,
            "note/index.html", routes);

        Assert.Contains($"![image](../assets/{Uri.EscapeDataString(imported.FileName)})", rewritten);
    }

    [Fact]
    public void RewritesAttachmentReferenceDefinitionsForGeneratedPageRoutes()
    {
        var imported = _attachments.Import(_notes, Path.Combine(_notes, "note.md"), _source);
        var routes = new Dictionary<string, string> { [imported.Path] = "assets/" + imported.FileName };
        var markdown = $"[manual][attachment]\n\n[attachment]: <{imported.RelativePath}> \"help\"\n";

        var rewritten = _attachments.RewriteManagedLinksForOutput(_notes, Path.Combine(_notes, "note.md"), markdown,
            "note/index.html", routes);

        Assert.Contains("[manual][attachment]", rewritten);
        Assert.Contains($"[attachment]: ../assets/{Uri.EscapeDataString(imported.FileName)} \"help\"", rewritten);
    }

    [Fact]
    public async Task PreviewServerServesOnlyExplicitLiveAttachmentTokens()
    {
        var imported = _attachments.Import(_notes, Path.Combine(_notes, "note.md"), _source);
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(output);
        await using var server = new PreviewServer();
        await server.StartAsync(output);
        var token = AttachmentService.LivePreviewToken(imported.Path);
        server.SetLivePreview("<img>", new Dictionary<string, string> { [token] = imported.Path }, _notes);
        using var client = new HttpClient();

        Assert.True(server.CanNavigate(new Uri(server.BaseUri!, "__tatami_live_assets/" + token)));
        var response = await client.GetAsync(new Uri(server.BaseUri!, "__tatami_live_assets/" + token));
        var missing = await client.GetAsync(new Uri(server.BaseUri!, "__tatami_live_assets/unknown"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?download=1")]
    [InlineData("#page=2")]
    [InlineData("?download=1#page=2")]
    public void ResolvesEncodedNestedPathsAndPreservesSuffixes(string suffix)
    {
        var note = Path.Combine(_notes, "nested", "note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        var asset = Path.Combine(_notes, AttachmentService.FolderName, "日本 語.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        File.WriteAllText(asset, "pdf");
        var url = "../.tatami-assets/" + Uri.EscapeDataString("日本 語.pdf") + suffix;
        var markdown = $"[first](<{url}>)\n[second](<{url}>)\n";

        Assert.True(AttachmentService.TryGetManagedPath(_notes, note, url, out var resolved));
        Assert.Equal(asset, resolved);
        Assert.Equal(new NoteAttachment(asset, "assets/日本 語.pdf"),
            Assert.Single(_attachments.FindReferenced(_notes, note, markdown)));
        var routes = new Dictionary<string, string> { [asset] = "assets/日本 語.pdf" };
        var expectedUrl = "../../assets/" + Uri.EscapeDataString("日本 語.pdf") + suffix;
        Assert.Equal($"[first]({expectedUrl})\n[second]({expectedUrl})\n",
            _attachments.RewriteManagedLinksForOutput(_notes, note, markdown, "guides/note/index.html", routes));
    }

    [Theory]
    [InlineData("https://example.com/.tatami-assets/a.pdf")]
    [InlineData("//example.com/.tatami-assets/a.pdf")]
    [InlineData("#heading")]
    [InlineData("?download=1")]
    [InlineData(".tatami-assets/../outside.pdf")]
    [InlineData(".tatami-assets/%2e%2e/outside.pdf")]
    public void IgnoresExternalEmptyAndOutsideAssetPaths(string url)
    {
        var note = Path.Combine(_notes, "note.md");
        var markdown = $"[link](<{url}>)\n";

        Assert.False(AttachmentService.TryGetManagedPath(_notes, note, url, out _));
        Assert.Empty(_attachments.FindReferenced(_notes, note, markdown));
        Assert.Equal(markdown, _attachments.RewriteManagedLinksForOutput(_notes, note, markdown,
            "note/index.html", new Dictionary<string, string>()));
    }

    [Fact]
    public void PreservesReferencedAttachmentValidationOrderAndSizeBoundary()
    {
        var note = Path.Combine(_notes, "note.md");
        var folder = Path.Combine(_notes, AttachmentService.FolderName);
        Directory.CreateDirectory(folder);
        var unsupported = Path.Combine(folder, "missing.exe");
        var invalid = Assert.Throws<InvalidDataException>(() =>
            _attachments.FindReferenced(_notes, note, "[bad](.tatami-assets/missing.exe)"));
        Assert.Equal($"サポートされていない添付形式です: {unsupported}", invalid.Message);
        var missing = Assert.Throws<FileNotFoundException>(() =>
            _attachments.FindReferenced(_notes, note, "[missing](.tatami-assets/missing.pdf)"));
        Assert.Equal(Path.Combine(folder, "missing.pdf"), missing.FileName);

        var asset = Path.Combine(folder, "large.pdf");
        using (var file = File.Create(asset)) file.SetLength(AttachmentService.MaximumFileSizeBytes);
        Assert.Single(_attachments.FindReferenced(_notes, note, "[limit](.tatami-assets/large.pdf)"));
        using (var file = File.OpenWrite(asset)) file.SetLength(AttachmentService.MaximumFileSizeBytes + 1);
        var oversized = Assert.Throws<InvalidDataException>(() =>
            _attachments.FindReferenced(_notes, note, "[large](.tatami-assets/large.pdf)"));
        Assert.Equal($"添付ファイルが100 MBを超えています: {asset}", oversized.Message);
    }

    [Fact]
    public void RejectsReferencedSymlinkBeforeCheckingItsFormat()
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(_notes, AttachmentService.FolderName);
        Directory.CreateDirectory(folder);
        File.CreateSymbolicLink(Path.Combine(folder, "linked.exe"), _source);

        Assert.Throws<IOException>(() => _attachments.FindReferenced(_notes, Path.Combine(_notes, "note.md"),
            "[link](.tatami-assets/linked.exe)"));
    }

    [Theory]
    [InlineData(".PDF", false)]
    [InlineData(".PNG", true)]
    public void ImportsEmptySafeStemWithNormalizedExtension(string extension, bool isImage)
    {
        var source = Path.Combine(_root, "   " + extension);
        File.WriteAllText(source, "content");

        var imported = _attachments.Import(_notes, Path.Combine(_notes, "note.md"), source);

        Assert.Equal("attachment" + extension.ToLowerInvariant(), imported.FileName);
        Assert.Equal(isImage, imported.IsImage);
        Assert.StartsWith(isImage ? "![" : "[", imported.Markdown);
        Assert.Equal("content", File.ReadAllText(imported.Path));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
