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

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
