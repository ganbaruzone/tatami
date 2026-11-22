using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class PrivateContentTests
{
    [Fact]
    public void NormalTextIsIncludedByDefault() =>
        Assert.Equal("# Public\n\nVisible paragraph", PrivateContentFilter.Remove("# Public\n\nVisible paragraph"));

    [Fact]
    public void RemovesParagraphsAndPrivateDefinitions()
    {
        var text = "before\n\n:::private\nSECRET\n\nAnother SECRET\n\n[x]: https://secret.example\n[^a]: SECRET FOOTNOTE\n:::\n\nafter\n";
        var result = PrivateContentFilter.Remove(text);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
        Assert.DoesNotContain("SECRET", result);
        Assert.DoesNotContain("secret.example", result);
        Assert.DoesNotContain(":::private", result);
    }

    [Fact]
    public void FencedCodeExamplesAreNotRemoved()
    {
        var text = "```markdown\n:::private\nExample\n:::\n```";
        Assert.Equal(text, PrivateContentFilter.Remove(text));
    }

    [Fact]
    public void UnclosedBlockFailsSafely() => Assert.Throws<InvalidDataException>(() =>
        PrivateContentFilter.Remove("Public\n\n:::private\nSECRET"));

    [Fact]
    public void HandlesQuotedAndMultipleBlocks()
    {
        var result = PrivateContentFilter.Remove("> :::private\n> SECRET\n> :::\n\nVisible\n\n:::private\nSECRET TWO\n:::");
        Assert.DoesNotContain("SECRET", result);
        Assert.Contains("Visible", result);
    }
}
