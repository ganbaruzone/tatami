namespace BuilderFixture;

public static class Marker
{
    public static async Task<int> Main(string[] args)
    {
        var content = Environment.GetEnvironmentVariable("TATAMI_CONTENT_DIR")!;
        var output = Environment.GetEnvironmentVariable("TATAMI_OUTPUT_DIR")!;
        if (args.Length > 1 && args[1] != output) return 19;
        Console.WriteLine("fixture standard output");
        Console.Error.WriteLine("fixture standard error");
        if (args[0] == "wait") { await Task.Delay(30000); return 0; }
        if (args[0] == "empty") return 0;
        Directory.CreateDirectory(output);
        var notes = string.Join('\n', Directory.GetFiles(content, "*.md").Select(File.ReadAllText));
        await File.WriteAllTextAsync(Path.Combine(output, "index.html"), "<html>" + notes + "</html>");
        Directory.CreateDirectory(Path.Combine(output, "_assets"));
        await File.WriteAllTextAsync(Path.Combine(output, "_assets/style.css"), "body { color: red }");
        if (args[0] == "link") File.CreateSymbolicLink(Path.Combine(output, "linked.html"), Path.Combine(content, "public.md"));
        return args[0] == "fail" ? 37 : 0;
    }
}
