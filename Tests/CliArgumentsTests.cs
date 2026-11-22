using Xunit;

namespace Tatami.Tests;

public sealed class CliArgumentsTests
{
    [Fact]
    public void ParsesBuildOptionsAndPositionals()
    {
        var success = CliArguments.TryParse(
            ["--quiet", "build", "--input", "notes", "--output", "site", "--json"],
            out var arguments,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(arguments);
        Assert.Equal(["build"], arguments.Positional);
        Assert.Equal("notes", arguments.Input);
        Assert.Equal("site", arguments.Output);
        Assert.True(arguments.Json);
        Assert.True(arguments.Quiet);
        Assert.False(arguments.Verbose);
    }

    [Theory]
    [InlineData(new[] { "--quiet", "--verbose" }, "--quiet と --verbose は同時に指定できません。")]
    [InlineData(new[] { "--input" }, "--input には値を指定してください。")]
    [InlineData(new[] { "--output", "--json" }, "--output には値を指定してください。")]
    [InlineData(new[] { "build", "--typo" }, "不明なオプションです: --typo")]
    [InlineData(new[] { "notes", "list", "--watch" }, "--watch は build コマンドでのみ使用できます。")]
    public void RejectsInvalidArguments(string[] values, string expectedError)
    {
        var success = CliArguments.TryParse(values, out var arguments, out var error);

        Assert.False(success);
        Assert.Null(arguments);
        Assert.Equal(expectedError, error);
    }
}
