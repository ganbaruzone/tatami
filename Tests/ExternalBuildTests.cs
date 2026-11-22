using System.Text.Json;
using Tatami.Models;
using Tatami.Services;
using Xunit;

namespace Tatami.Tests;

public sealed class ExternalBuildTests : IDisposable
{
    private readonly string _root;
    private readonly string _notes;
    private readonly string _output;
    private readonly string _project;
    public ExternalBuildTests()
    {
        var temp = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/")) temp = "/private" + temp;
        _root = Path.Combine(temp, "tatami external " + Guid.NewGuid());
        _notes = Path.Combine(_root, "notes");
        _output = Path.Combine(_root, "site");
        _project = Path.Combine(_root, "project with spaces");
        Directory.CreateDirectory(_notes);
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_notes, "public.md"), "---\npublish: true\ntitle: Public\ncustom: retained\n---\nVISIBLE\n\n:::private\nSECRET PARAGRAPH\n:::\n");
        File.WriteAllText(Path.Combine(_notes, "private.md"), "SECRET NOTE");
    }

    private SiteConfig Config(string mode = "success", int timeout = 30) => new(_notes, _output)
    {
        Builder = "external", External = new ExternalBuildOptions
        {
            ProjectPath = _project, Executable = "dotnet",
            Arguments = [typeof(BuilderFixture.Marker).Assembly.Location, mode, "{output}"], TimeoutSeconds = timeout
        }
    };

    [Fact]
    public async Task RunsCommandExportsOnlyPublicTextAndPublishesAssets()
    {
        var lines = new List<string>();
        var result = await new BuildCoordinator().BuildAsync(Config(), new InlineProgress(lines));
        Assert.Equal(1, result.PublishedCount);
        Assert.Equal(1, result.PrivateCount);
        var html = await File.ReadAllTextAsync(Path.Combine(_output, "index.html"));
        Assert.Contains("VISIBLE", html);
        Assert.Contains("custom: retained", html);
        Assert.DoesNotContain("SECRET", html);
        Assert.True(File.Exists(Path.Combine(_output, "_assets/style.css")));
        Assert.Contains(lines, line => line.Contains("standard output"));
        Assert.Contains(lines, line => line.Contains("standard error"));
        Assert.Contains("SECRET", await File.ReadAllTextAsync(Path.Combine(_notes, "public.md")));
        Assert.Equal("/", Assert.Single(await PreviewCatalog.ReadAsync(_output)).Name);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("empty")]
    public async Task FailedOrEmptyBuildPreservesOutput(string mode)
    {
        await new BuildCoordinator().BuildAsync(Config());
        var before = await File.ReadAllTextAsync(Path.Combine(_output, "index.html"));
        if (mode == "empty") await Assert.ThrowsAsync<InvalidDataException>(() => new BuildCoordinator().BuildAsync(Config(mode)));
        else await Assert.ThrowsAsync<IOException>(() => new BuildCoordinator().BuildAsync(Config(mode)));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_output, "index.html")));
    }

    [Fact]
    public async Task CanCancelAndTimeoutProcesses()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => new BuildCoordinator().BuildAsync(Config("wait", 1)));
        using var cancel = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BuildCoordinator().BuildAsync(Config("wait"), cancellationToken: cancel.Token));
        Assert.False(Directory.Exists(_output));
    }

    [Fact]
    public async Task RejectsGeneratedSymlinks()
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAnyAsync<IOException>(() => new BuildCoordinator().BuildAsync(Config("link")));
        Assert.False(Directory.Exists(_output));
    }

    [Fact]
    public async Task LoopbackPreviewServesRootAssetsAndProtectsOtherFiles()
    {
        await new BuildCoordinator().BuildAsync(Config());
        await File.WriteAllTextAsync(Path.Combine(_output, "unowned.txt"), "SECRET");
        await using var server = new PreviewServer();
        await server.StartAsync(_output);
        using var client = new HttpClient();
        Assert.Contains("VISIBLE", await client.GetStringAsync(server.BaseUri));
        Assert.Contains("color: red", await client.GetStringAsync(new Uri(server.BaseUri!, "/_assets/style.css")));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync(new Uri(server.BaseUri!, "/unowned.txt"))).StatusCode);
        Assert.True(server.CanNavigate(server.BaseUri!));
        Assert.False(server.CanNavigate(new Uri("https://example.com")));
        Assert.False(server.CanNavigate(new Uri(server.BaseUri!, "/_assets/style.css")));
    }

    [Fact]
    public async Task SettingsRoundTripExternalOptionsAndLegacyDefaults()
    {
        var old = JsonSerializer.Deserialize<SiteConfig>("{\"InputPath\":\"notes\",\"OutputPath\":\"site\"}")!;
        Assert.Equal("builtin", old.Builder);
        var settings = new WorkspaceSettings(Path.Combine(_root, "settings.json"));
        await settings.SaveAsync(Config());
        var restored = (await settings.LoadAsync())!;
        Assert.Equal("external", restored.Builder);
        Assert.Equal(_project, restored.External.ProjectPath);
        Assert.Equal(Config().External.Arguments, restored.External.Arguments);
    }

    private sealed class InlineProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
