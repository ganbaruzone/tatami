using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class NoteTemplateServiceTests
{
    [Theory]
    [InlineData("blank", "publish: false")]
    [InlineData("article", "tags: []")]
    [InlineData("meeting", "tags: [meeting]")]
    [InlineData("daily", "tags: [daily]")]
    public void RendersFrontMatterAndBodyVariables(string id, string expected)
    {
        var contents = new NoteTemplateService().Render(id, "Planner: O'Brien", new DateOnly(2026, 11, 23));
        var parsed = new NoteRepository().Parse("note.md", contents);

        Assert.Equal("Planner: O'Brien", parsed.Metadata.Title);
        Assert.Equal(new DateOnly(2026, 11, 23), parsed.Metadata.Date);
        Assert.False(parsed.Metadata.Publish);
        Assert.Contains(expected, contents);
        if (id == "blank") Assert.True(string.IsNullOrWhiteSpace(parsed.Markdown));
        else Assert.Contains("# Planner: O'Brien", parsed.Markdown);
    }

    [Fact]
    public void UnknownTemplateIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new NoteTemplateService().Render("custom", "Title", new DateOnly(2026, 11, 23)));
    }
}
