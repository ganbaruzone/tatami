using Tatami.Models;
using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class SiteBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly string _input;
    private readonly string _output;
    private readonly SiteBuilder _builder = new();

    public SiteBuilderTests()
    {
        var temp = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/")) temp = "/private" + temp;
        _root = Path.Combine(temp, "tatami-tests-" + Guid.NewGuid());
        _input = Path.Combine(_root, "notes");
        _output = Path.Combine(_root, "output");
        Directory.CreateDirectory(_input);
    }
    private void Note(string name, string text)
    {
        var path = Path.Combine(_input, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private string Html(string slug) => File.ReadAllText(Path.Combine(_output, slug, "index.html"));

    [Fact]
    public async Task PublishesOnlyExplicitPublicNotesRecursively()
    {
        Note("nested/article.md", "---\npublish: true\n---\n# Hello");
        Note("private.md", "---\npublish: false\n---\nSECRET");
        Note("plain.md", "SECRET");
        var result = await _builder.BuildAsync(_input, _output);
        Assert.Equal(1, result.PublishedCount);
        Assert.Equal(2, result.PrivateCount);
        Assert.Contains("Hello</h1>", Html("article"));
        Assert.False(Directory.Exists(Path.Combine(_output, "private")));
        Assert.False(Directory.Exists(Path.Combine(_output, "plain")));
        Assert.DoesNotContain("SECRET", Html("article"));
    }

    [Fact]
    public async Task GeneratesPublicIndexTagAndYearArchivesWithConfiguredOrderAndPaths()
    {
        Note("first.md", "---\ntitle: First\ndate: 2025-04-01\ntags: [CSharp]\npublish: true\n---\nfirst");
        Note("second.md", "---\ntitle: Second\ndate: 2026-03-01\ntags: [CSharp, Notes]\npublish: true\n---\nsecond");
        Note("private.md", "---\ntitle: Hidden\ntags: [CSharp]\npublish: false\n---\nhidden");

        await _builder.BuildAsync(new SiteConfig(_input, _output)
        {
            Archives = new SiteArchiveOptions { IndexPath = "articles.html", TagDirectory = "by-tag", DateDirectory = "by-year", SortBy = "date", Descending = true }
        });

        var index = File.ReadAllText(Path.Combine(_output, "articles.html"));
        var tag = File.ReadAllText(Path.Combine(_output, "by-tag", "CSharp", "index.html"));
        var year = File.ReadAllText(Path.Combine(_output, "by-year", "2026", "index.html"));
        Assert.True(index.IndexOf("Second", StringComparison.Ordinal) < index.IndexOf("First", StringComparison.Ordinal));
        Assert.DoesNotContain("Hidden", index);
        Assert.DoesNotContain("Hidden", tag);
        Assert.Contains("first/index.html", tag);
        Assert.Contains("second/index.html", year);
        Assert.DoesNotContain("first/index.html", year);
    }

    [Fact]
    public async Task RendersMetadataTablesCodeAndEscapesHtml()
    {
        Note("a.md", "---\ntitle: '<script>alert(1)</script> & title'\ndate: 2026-11-22\ntags: [CSharp, '<img onerror=alert(1)>']\npublish: true\nslug: custom\n---\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```cs\nvar x = 1;\n```\n\n<script>alert(2)</script>\n\nhello **world**\n\n[bad](javascript:alert%281%29)\n\ntext{onclick=alert(3)}");
        await _builder.BuildAsync(_input, _output);
        var html = Html("custom");
        Assert.Contains("<table>", html);
        Assert.Contains("<pre><code class=\"language-cs\">", html);
        Assert.Contains("<strong>world</strong>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("&amp;lt;script", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain(" onclick=", html);
        Assert.Contains("2026-11-22", html);
        Assert.Contains("CSharp", html);
        Assert.Contains("href=\"../style.css\"", html);
        Assert.True(File.Exists(Path.Combine(_output, "style.css")));
    }

    [Fact]
    public async Task RewritesMarkdownNoteLinksToPublishedHtmlRoutes()
    {
        Note("current.md", "---\npublish: true\n---\n[Guide](guides/start%20here.md#first-heading)\n");
        Note("guides/start here.md", "---\npublish: true\nslug: guide\n---\n# First Heading\n");
        Note("private.md", "---\npublish: false\n---\nprivate\n");
        await _builder.BuildAsync(_input, _output);

        var html = Html("current");

        Assert.Contains("href=\"../guide/index.html#first-heading\"", html);
    }

    [Fact]
    public async Task PublishesOnlyAttachmentsReferencedByPublicContent()
    {
        var assetRoot = Path.Combine(_input, AttachmentService.FolderName);
        Directory.CreateDirectory(assetRoot);
        var publicAsset = Path.Combine(assetRoot, "public.png");
        var privateAsset = Path.Combine(assetRoot, "private.png");
        var orphanAsset = Path.Combine(assetRoot, "orphan.png");
        File.WriteAllBytes(publicAsset, [1, 2, 3]);
        File.WriteAllBytes(privateAsset, [4, 5, 6]);
        File.WriteAllBytes(orphanAsset, [7, 8, 9]);
        Note("article.md", "---\npublish: true\n---\n![Public](.tatami-assets/public.png)\n\n::: private\n![Private](.tatami-assets/private.png)\n:::\n");
        Note("private-note.md", "---\npublish: false\n---\n![Private](.tatami-assets/private.png)\n");

        await _builder.BuildAsync(_input, _output);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_output, "assets", "public.png")));
        Assert.False(File.Exists(Path.Combine(_output, "assets", "private.png")));
        Assert.False(File.Exists(Path.Combine(_output, "assets", "orphan.png")));
        Assert.Contains("src=\"../assets/public.png\"", Html("article"));
        Assert.DoesNotContain("private.png", Html("article"));
    }

    [Fact]
    public async Task MissingPublicAttachmentStopsBuildBeforeChangingOutput()
    {
        Note("article.md", "---\npublish: true\n---\n![Missing](.tatami-assets/missing.png)\n");

        await Assert.ThrowsAsync<FileNotFoundException>(() => _builder.BuildAsync(_input, _output));

        Assert.False(Directory.Exists(_output));
    }

    [Fact]
    public async Task DuplicateSlugLeavesPublishedSiteUnchanged()
    {
        Note("one.md", "---\npublish: true\nslug: same\n---\nOriginal");
        await _builder.BuildAsync(_input, _output);
        var before = Html("same");
        Note("two.md", "---\npublish: true\nslug: SAME\n---\nReplacement");
        await Assert.ThrowsAsync<InvalidDataException>(() => _builder.BuildAsync(_input, _output));
        Assert.Equal(before, Html("same"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    [InlineData("CON")]
    [InlineData("")]
    public async Task InvalidSlugCannotWriteOutsideOutput(string slug)
    {
        Note("bad.md", $"---\npublish: true\nslug: '{slug}'\n---\nbody");
        await Assert.ThrowsAsync<InvalidDataException>(() => _builder.BuildAsync(_input, _output));
        Assert.False(Directory.Exists(_output));
        Assert.False(Directory.Exists(Path.Combine(_root, "escape")));
    }

    [Fact]
    public async Task OutputUnderInputIsExcluded()
    {
        var output = Path.Combine(_input, "output");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "ignore.md"), "---\npublish: true\n---\nIgnore");
        Note("article.MD", "---\npublish: true\n---\nbody");
        var result = await _builder.BuildAsync(_input, output);
        Assert.Equal(1, result.PublishedCount);
        Assert.False(Directory.Exists(Path.Combine(output, "ignore")));
    }

    [Fact]
    public async Task UnpublishingRemovesOnlyOwnedPage()
    {
        Note("one.md", "---\npublish: true\n---\nbody");
        await _builder.BuildAsync(_input, _output);
        File.WriteAllText(Path.Combine(_output, "keep.txt"), "keep");
        Note("one.md", "---\npublish: false\n---\nbody");
        await _builder.BuildAsync(_input, _output);
        Assert.False(File.Exists(Path.Combine(_output, "one/index.html")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_output, "keep.txt")));
    }

    [Fact]
    public async Task ExistingUnownedAndModifiedFilesAreProtected()
    {
        Note("a.md", "---\npublish: true\n---\nbody");
        Directory.CreateDirectory(_output);
        var css = Path.Combine(_output, "style.css");
        File.WriteAllText(css, "mine");
        await Assert.ThrowsAsync<IOException>(() => _builder.BuildAsync(_input, _output));
        Assert.Equal("mine", File.ReadAllText(css));
        Assert.False(Directory.Exists(Path.Combine(_output, "a")));
        File.Delete(css);
        await _builder.BuildAsync(_input, _output);
        File.WriteAllText(css, "manual edit");
        await Assert.ThrowsAsync<IOException>(() => _builder.BuildAsync(_input, _output));
        Assert.Equal("manual edit", File.ReadAllText(css));
    }

    [Fact]
    public async Task InvalidFrontMatterPreservesPreviousOutput()
    {
        Note("a.md", "---\npublish: true\n---\nOriginal");
        await _builder.BuildAsync(_input, _output);
        var original = Html("a");
        Note("broken.md", "---\npublish: [\n---\nbody");
        await Assert.ThrowsAsync<InvalidDataException>(() => _builder.BuildAsync(_input, _output));
        Assert.Equal(original, Html("a"));
    }

    [Fact]
    public async Task RejectsOutputSymlinksAndSkipsInputLinks()
    {
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "secret.md"), "---\npublish: true\n---\nsecret");
        Directory.CreateSymbolicLink(Path.Combine(_input, "linked"), external);
        Note("a.md", "---\npublish: true\n---\nbody");
        var result = await _builder.BuildAsync(_input, _output);
        Assert.Equal(1, result.PublishedCount);
        Directory.CreateSymbolicLink(Path.Combine(_output, "escape"), external);
        Note("b.md", "---\npublish: true\nslug: escape\n---\nbody");
        await Assert.ThrowsAsync<IOException>(() => _builder.BuildAsync(_input, _output));
        Assert.False(File.Exists(Path.Combine(external, "index.html")));
    }

    [Fact]
    public async Task RejectsInputAsOutputOrItsParent()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _builder.BuildAsync(_input, _input));
        await Assert.ThrowsAsync<InvalidDataException>(() => _builder.BuildAsync(_input, _root));
    }

    [Fact]
    public async Task ConfigRoundTripsAndBuildsWithoutUi()
    {
        var path = Path.Combine(_root, "site.json");
        var config = new SiteConfig(_input, _output);
        await config.SaveAsync(path);
        Assert.Equivalent(config, await SiteConfig.LoadAsync(path));
        await _builder.BuildAsync(await SiteConfig.LoadAsync(path));
    }

    [Fact]
    public async Task WriteFailureRollsBackEarlierWrites()
    {
        if (OperatingSystem.IsWindows()) return;
        Note("a.md", "---\npublish: true\n---\noriginal a");
        Note("b.md", "---\npublish: true\n---\noriginal b");
        await _builder.BuildAsync(_input, _output);
        var a = Html("a");
        var b = Html("b");
        var manifest = File.ReadAllText(Path.Combine(_output, ".tatami-manifest.json"));
        Note("a.md", "---\npublish: true\n---\nchanged a");
        Note("b.md", "---\npublish: true\n---\nchanged b");
        var folder = Path.Combine(_output, "b");
        var mode = File.GetUnixFileMode(folder);
        try
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                try { await _builder.BuildAsync(_input, _output); }
                catch (UnauthorizedAccessException ex) { throw new IOException("permission denied", ex); }
            });
            Assert.Equal(a, Html("a"));
            Assert.Equal(b, Html("b"));
            Assert.Equal(manifest, File.ReadAllText(Path.Combine(_output, ".tatami-manifest.json")));
        }
        finally { File.SetUnixFileMode(folder, mode); }
    }

    [Fact]
    public async Task CaseOnlySlugChangeDoesNotInvalidateManifest()
    {
        Note("a.md", "---\npublish: true\nslug: first\n---\nOriginal");
        await _builder.BuildAsync(_input, _output);
        Note("a.md", "---\npublish: true\nslug: FIRST\n---\nChanged");
        await Assert.ThrowsAsync<InvalidDataException>(() => _builder.BuildAsync(_input, _output));
        Assert.Contains("Original", Html("first"));
    }

    [Fact]
    public async Task InvalidTemplatePreservesSite()
    {
        Note("a.md", "---\npublish: true\n---\nOriginal");
        await _builder.BuildAsync(_input, _output);
        var original = Html("a");
        var templates = Path.Combine(_root, "templates");
        Directory.CreateDirectory(templates);
        File.WriteAllText(Path.Combine(templates, "default.html"), "{{ if }}");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SiteBuilder(templates).BuildAsync(_input, _output));
        Assert.Equal(original, Html("a"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
