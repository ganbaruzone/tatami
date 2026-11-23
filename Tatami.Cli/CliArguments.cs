internal sealed record CliArguments(
    IReadOnlyList<string> Positional,
    string? Input,
    string? Output,
    string? ConfigPath,
    string? Directory,
    bool Json,
    bool Watch,
    bool Fix,
    bool Quiet,
    bool Verbose)
{
    public static bool TryParse(string[] args, out CliArguments? parsed, out string? error)
    {
        var positional = new List<string>();
        string? input = null;
        string? output = null;
        string? configPath = null;
        string? directory = null;
        var json = false;
        var watch = false;
        var fix = false;
        var quiet = args.Contains("--quiet");
        var verbose = args.Contains("--verbose");

        if (quiet && verbose)
        {
            parsed = null;
            error = "--quiet と --verbose は同時に指定できません。";
            return false;
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--quiet" or "--verbose") continue;
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] == "--watch") { watch = true; continue; }
            if (args[i] == "--fix") { fix = true; continue; }
            if (args[i] is "--input" or "--output" or "--config" or "--directory")
            {
                var option = args[i];
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    parsed = null;
                    error = $"{option} には値を指定してください。";
                    return false;
                }

                if (option == "--input") input = args[i];
                else if (option == "--output") output = args[i];
                else if (option == "--config") configPath = args[i];
                else directory = args[i];
                continue;
            }

            if (args[i].StartsWith('-'))
            {
                parsed = null;
                error = $"不明なオプションです: {args[i]}";
                return false;
            }

            positional.Add(args[i]);
        }

        if (watch && !(positional.Count == 1 && positional[0] == "build"))
        {
            parsed = null;
            error = "--watch は build コマンドでのみ使用できます。";
            return false;
        }

        if (fix && !(positional.Count == 1 && positional[0] == "doctor"))
        {
            parsed = null;
            error = "--fix は doctor コマンドでのみ使用できます。";
            return false;
        }

        parsed = new CliArguments(positional, input, output, configPath, directory, json, watch, fix, quiet, verbose);
        error = null;
        return true;
    }
}
