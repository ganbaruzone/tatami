using Tatami.Models;
using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class NoteLinkServiceTests : IDisposable
{
    private readonly string _root;
    private readonly NoteLinkService _links = new();

    public NoteLinkServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tatami-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void FindsNotesAndHeadingTargetsUsingRenderedAnchorIds()
    {
        var path = Write("guides/start.md", "---\ntitle: Start Here\n---\n# First Heading\n## 日本語の見出し\n");

        var targets = _links.FindTargets(_root);

        Assert.Contains(targets, target => target.Path == path && target.Title == "Start Here" && target.HeadingId == null);
        Assert.Contains(targets, target => target.Path == path && target.HeadingId == "first-heading");
        Assert.Contains(targets, target => target.Path == path && target.HeadingTitle == "日本語の見出し");
    }

    [Fact]
    public void CreatesPortableRelativeLinksIncludingHeadingFragments()
    {
        var source = Write("docs/index.md", "");
        var target = Write("docs/guide/Start Here.md", "# Section\n");

        var link = _links.CreateMarkdownLink(source, new NoteLinkTarget(target, "Start Here", "section", "Section"), "Read this");

        Assert.Equal("[Read this](<guide/Start%20Here.md#section>)", link);
    }

    [Fact]
    public void FindsInlineMarkdownLinksAtDocumentOffsetsButIgnoresImagesAndCode()
    {
        var source = Write("source.md", "");
        var markdown = "---\npublish: true\n---\n[guide](guide.md#section)\n\n`[code](guide.md)`\n\n![image](image.png)\n";

        Assert.True(_links.TryGetLinkAtOffset(source, markdown, markdown.IndexOf("[guide]", StringComparison.Ordinal) + 2, out var url));
        Assert.Equal("guide.md#section", url);
        Assert.False(_links.TryGetLinkAtOffset(source, markdown, markdown.IndexOf("[code]", StringComparison.Ordinal) + 2, out _));
        Assert.False(_links.TryGetLinkAtOffset(source, markdown, markdown.IndexOf("![image]", StringComparison.Ordinal) + 3, out _));
    }

    [Fact]
    public void ResolvesWorkspaceNoteLinksAndWebUris()
    {
        var source = Write("source.md", "");
        var target = Write("guides/guide.md", "# Section\n");

        Assert.True(_links.TryResolveNotePath(_root, source, "guides/guide.md#section", out var resolved, out var fragment));
        Assert.Equal(target, resolved);
        Assert.Equal("section", fragment);
        Assert.False(_links.TryResolveNotePath(_root, source, "../outside.md", out _, out _));
        Assert.True(NoteLinkService.TryGetWebUri("https://example.com/path", out var webUri));
        Assert.Equal("https", webUri.Scheme);
        Assert.False(NoteLinkService.TryGetWebUri("file:///tmp/note.md", out _));
    }

    [Fact]
    public void ReportsMissingNoteAndHeadingButIgnoresExternalLinks()
    {
        Write("target.md", "# Existing Section\n");
        var source = Write("source.md", "# Source\n[missing](missing.md)\n[bad heading](target.md#absent)\n[ok](target.md#existing-section)\n[web](https://example.com)\n");

        var broken = _links.CheckLinks(_root);

        Assert.Equal(2, broken.Count);
        Assert.Contains(broken, link => link.SourcePath == source && link.Url == "missing.md");
        Assert.Contains(broken, link => link.SourcePath == source && link.Url == "target.md#absent");
    }

    [Fact]
    public void UpdatesInlineAndReferenceLinksAfterRename()
    {
        var source = Write("folder/source.md", "");
        var oldTarget = Write("folder/old.md", "# Old\n");
        var newTarget = Path.Combine(Path.GetDirectoryName(oldTarget)!, "new.md");
        var markdown = "[inline](old.md#old)\n\n[ref]: <old.md#old> \"title\"\n\n[reference][ref]\n";

        var updated = _links.UpdateRenamedTargetLinks(markdown, source, oldTarget, newTarget);

        Assert.Contains("[inline](new.md#old)", updated);
        Assert.Contains("[ref]: new.md#old \"title\"", updated);
        Assert.Contains("[reference][ref]", updated);
    }

    [Fact]
    public void FindsIncomingLinksAndUnlinkedMentionsButIgnoresLinkedLabelsAndCode()
    {
        var target = Write("notes/target.md", "---\ntitle: Aurora Guide\n---\n# Guide\n");
        var source = Write("source.md", "[Aurora Guide](notes/target.md)\nAurora Guide is useful.\n`Aurora Guide` in code.\n```\nAurora Guide\n```\n");

        var found = _links.FindIncomingReferences(_root, target);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, reference => reference.SourcePath == source && reference.IsLink && reference.Line == 1);
        Assert.Contains(found, reference => reference.SourcePath == source && !reference.IsLink && reference.Line == 2);
    }

    [Fact]
    public void UsesOpenDocumentTextWhenFindingIncomingReferences()
    {
        var target = Write("target.md", "# Target\n");
        var source = Write("source.md", "No reference yet.\n");

        var found = _links.FindIncomingReferences(_root, target,
            new Dictionary<string, string> { [source] = "Target is mentioned here.\n" });

        Assert.Single(found);
        Assert.False(found[0].IsLink);
    }

    [Fact]
    public void RewritesIncomingAndMovedNoteLinksWhenMovingAcrossFolders()
    {
        var target = Write("target.md", "# Target\n[Source](folder/source.md)\n[Source][ref]\n\n[ref]: <folder/source.md>\n");
        var source = Write("folder/source.md", "[Target](../target.md)\n");
        var movedTarget = Path.Combine(_root, "folder", "target.md");

        var updates = _links.PrepareRenameUpdates(_root, target, movedTarget);

        Assert.Contains("target.md", updates[source]);
        Assert.Contains("source.md", updates[target]);
        Assert.Contains("[ref]: <source.md>", updates[target]);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("日本 語", "#%E6%97%A5%E6%9C%AC%20%E8%AA%9E")]
    public void EscapesRelativeDestinationsAndLabels(string? heading, string suffix)
    {
        var source = Write("docs/source.md", "");
        var target = Write("日本 語#%.md", "");
        const string destination = "../%E6%97%A5%E6%9C%AC%20%E8%AA%9E%23%25.md";

        Assert.Equal(destination, NoteLinkService.GetRelativePath(source, target));
        Assert.Equal("[a\\\\b\\]](<" + destination + suffix + ">)",
            _links.CreateMarkdownLink(source, new NoteLinkTarget(target, "Title", heading), "a\\b]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?mode=read")]
    [InlineData("#section")]
    [InlineData("?mode=read#section")]
    [InlineData("#section?mode=read")]
    public void RenamePreservesUrlSuffixAndUnrelatedLinks(string suffix)
    {
        var source = Write("source.md", "");
        var oldTarget = Write("old.md", "");
        var newTarget = Path.Combine(_root, "new name.md");
        var markdown = $"[rename](old.md{suffix})\n[other](other.md)\n![image](old.md{suffix})\n";

        var updated = _links.UpdateRenamedTargetLinks(markdown, source, oldTarget, newTarget);

        Assert.Equal($"[rename](new%20name.md{suffix})\n[other](other.md)\n![image](old.md{suffix})\n", updated);
    }

    [Fact]
    public void ChecksUnsavedLinksAndHeadingsWithFrontMatterLineNumbers()
    {
        var target = Write("target.md", "# Old\n");
        var source = Write("source.md", "[disk](absent.md)\n");
        var openDocuments = new Dictionary<string, string>
        {
            [target] = "# New\n",
            [source] = "---\ntitle: Source\n---\n[ok](target#new)\n[old](target.md#old)\n[missing](missing.md)\n"
        };

        var broken = _links.CheckLinks(_root, openDocuments);

        Assert.Collection(broken,
            link => Assert.Equal(new BrokenNoteLink(source, 5, "target.md#old", "見出し #old がありません"), link),
            link => Assert.Equal(new BrokenNoteLink(source, 6, "missing.md", "リンク先ノートがありません"), link));
    }

    [Fact]
    public void ChecksAttachmentErrorsBeforeSkippingImages()
    {
        Write(".tatami-assets/ok.png", "image");
        var source = Write("source.md", "![ok](.tatami-assets/ok.png)\n![unsupported](.tatami-assets/missing.exe)\n![missing](.tatami-assets/missing.png)\n![ordinary](missing.png)\n");

        var broken = _links.CheckLinks(_root);

        Assert.Collection(broken,
            link => Assert.Equal(new BrokenNoteLink(source, 2, ".tatami-assets/missing.exe", "添付できないファイル形式です"), link),
            link => Assert.Equal(new BrokenNoteLink(source, 3, ".tatami-assets/missing.png", "添付ファイルがありません"), link));
    }

    [Fact]
    public void RenameUsesUnsavedTextWithoutWritingFiles()
    {
        var target = Write("old.md", "# Old\n");
        var source = Write("source.md", "Disk content\n");
        var updated = _links.PrepareRenameUpdates(_root, target, Path.Combine(_root, "new.md"),
            new Dictionary<string, string> { [source] = "[unsaved](old.md)\n" });

        Assert.Single(updated);
        Assert.Equal("[unsaved](new.md)\n", updated[source]);
        Assert.Equal("Disk content\n", File.ReadAllText(source));
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(Path.Combine(_root, "new.md")));
    }

    [Theory]
    [InlineData("other.md", "first", "first")]
    [InlineData("note.md", "second", "second")]
    public void RejectsDuplicateOutputRoutesAndSourcePaths(string secondPath, string secondSlug, string errorSlug)
    {
        var first = new Note(Path.Combine(_root, "note.md"), new NoteMetadata { Publish = true, Slug = "first" }, "");
        var second = new Note(Path.Combine(_root, secondPath), new NoteMetadata { Publish = true, Slug = secondSlug }, "");

        var error = Assert.Throws<InvalidDataException>(() => _links.BuildPublishedRoutes([first, second]));

        Assert.Equal($"slugが重複しています: {errorSlug}", error.Message);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
