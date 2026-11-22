using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class PreviewAndSettingsTests : IDisposable
{
    private readonly string _root;
    public PreviewAndSettingsTests()
    {
        var temp = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/")) temp = "/private" + temp;
        _root = Path.Combine(temp, "tatami-preview-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task RemembersInputAndOutputIndependently()
    {
        var settings = new WorkspaceSettings(Path.Combine(_root, "settings/workspace.json"));
        Assert.Null(await settings.LoadAsync());
        await settings.SaveAsync("/notes", null);
        Assert.Equal("/notes", (await settings.LoadAsync())!.InputPath);
        await settings.SaveAsync("/notes", "/site");
        var reopened = await new WorkspaceSettings(Path.Combine(_root, "settings/workspace.json")).LoadAsync();
        Assert.Equal("/notes", reopened!.InputPath);
        Assert.Equal("/site", reopened.OutputPath);
    }

    [Fact]
    public async Task BuiltHtmlOmitsPrivateReferencesAndCatalogListsOnlyGeneratedPages()
    {
        var notes = Path.Combine(_root, "notes");
        var site = Path.Combine(_root, "site");
        Directory.CreateDirectory(notes);
        var markdown = "---\npublish: true\nslug: guide\n---\n# Visible\n\n[ref][x] and footnote[^a]\n\n:::private\nSECRET_BODY\n\n[x]: https://SECRET_LINK.example\n[^a]: SECRET_FOOTNOTE\n:::\n\nAfter\n";
        var source = Path.Combine(notes, "guide.md");
        await File.WriteAllTextAsync(source, markdown);
        await new SiteBuilder().BuildAsync(notes, site);
        var pages = await PreviewCatalog.ReadAsync(site);
        var page = Assert.Single(pages);
        Assert.Equal("guide", page.Name);
        var html = await File.ReadAllTextAsync(page.FullPath);
        Assert.DoesNotContain("SECRET", html);
        Assert.Contains("Visible", html);
        Assert.Contains("After", html);
        Assert.Equal(markdown, await File.ReadAllTextAsync(source));
        Assert.True(PreviewCatalog.CanNavigate(new Uri(page.FullPath), site));
        Assert.False(PreviewCatalog.CanNavigate(new Uri(source), site));
        Assert.False(PreviewCatalog.CanNavigate(new Uri("https://example.com"), site));
        await File.WriteAllTextAsync(Path.Combine(site, "unowned.html"), "not generated");
        Assert.Single(await PreviewCatalog.ReadAsync(site));
        await File.WriteAllTextAsync(source, markdown + "\n:::private\nUNCLOSED");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SiteBuilder().BuildAsync(notes, site));
        Assert.Equal(html, await File.ReadAllTextAsync(page.FullPath));
    }

    [Fact]
    public async Task CorruptCatalogCannotEscapeOutput()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".tatami-manifest.json"), "{\"../escape/index.html\":\"hash\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => PreviewCatalog.ReadAsync(_root));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
