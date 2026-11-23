using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class SlashCommandServiceTests
{
    private readonly SlashCommandService _commands = new();

    [Fact]
    public void ListsInsertionCommandsForDiagramPrivateRegionImageAndMarkdownBlocks()
    {
        Assert.Contains(_commands.Commands, command => command.Id == "diagram" && command.Insertion.Contains("```mermaid"));
        Assert.Contains(_commands.Commands, command => command.Id == "private" && command.Insertion.StartsWith(":::private\n", StringComparison.Ordinal));
        Assert.Contains(_commands.Commands, command => command.Id == "image");
        Assert.Contains(_commands.Commands, command => command.Id == "table");
    }

    [Theory]
    [InlineData("mermaid", "diagram")]
    [InlineData("privacy", "private")]
    [InlineData("picture", "image")]
    public void FiltersBySearchTerms(string query, string expectedId)
    {
        Assert.Contains(_commands.Filter(query), command => command.Id == expectedId);
    }

    [Fact]
    public void DiagramAndPrivateTemplatesSelectEditablePlaceholders()
    {
        Assert.Equal("開始", Assert.Single(_commands.Filter("diagram")).Placeholder);
        Assert.Contains("非公開の内容", Assert.Single(_commands.Filter("private")).Insertion);
        var table = Assert.Single(_commands.Filter("table"));
        Assert.Equal("セル内容", table.Placeholder);
        Assert.Contains("セル内容", table.Insertion);
        Assert.Empty(_commands.Filter("no-match"));
    }
}
