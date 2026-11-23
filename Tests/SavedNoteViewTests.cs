using Tatami.Models;
using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class SavedNoteViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tatami-views-" + Guid.NewGuid().ToString("N"));

    public SavedNoteViewTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AppliesTagPublicationDateFolderAndSortFilters()
    {
        var first = Note("guides/first.md", "First", new DateOnly(2025, 1, 2), true, "CSharp", "Notes");
        var second = Note("guides/second.md", "Second", new DateOnly(2025, 1, 3), false, "CSharp");
        var third = Note("drafts/third.md", "Third", new DateOnly(2025, 1, 4), true, "CSharp");
        var service = new SavedNoteViewService(_root, Path.Combine(_root, "saved.json"));
        var view = new SavedNoteView("recent guides", "csharp", "public", new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 5), "guides", "title", true);

        var results = service.Apply([first, second, third], view, _root);

        Assert.Equal([first], results);
    }

    [Fact]
    public void SavesAndOverwritesViewsByNameIgnoringCase()
    {
        var path = Path.Combine(_root, "saved.json");
        var store = new SavedNoteViewService(_root, path);
        store.Save(new SavedNoteView("Work", Tag: "one"));
        store.Save(new SavedNoteView("work", Tag: "two", SortBy: "path"));

        var saved = Assert.Single(store.Load());
        Assert.Equal("work", saved.Name);
        Assert.Equal("two", saved.Tag);
        Assert.Equal("path", saved.SortBy);
    }

    [Fact]
    public void SortsByTitleAndDateInBothDirections()
    {
        var alpha = Note("z.md", "Alpha", new DateOnly(2025, 1, 3), true);
        var beta = Note("a.md", "Beta", new DateOnly(2025, 1, 2), true);
        var service = new SavedNoteViewService(_root, Path.Combine(_root, "views.json"));

        var titleAscending = service.Apply([beta, alpha], new SavedNoteView("titles", SortBy: "title", Descending: false), _root);
        var dateDescending = service.Apply([alpha, beta], new SavedNoteView("dates", SortBy: "date"), _root);

        Assert.Equal([alpha, beta], titleAscending);
        Assert.Equal([alpha, beta], dateDescending);
    }

    private Note Note(string relativePath, string title, DateOnly date, bool publish, params string[] tags) =>
        new(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            new NoteMetadata { Title = title, Date = date, Publish = publish, Tags = tags }, "");

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
