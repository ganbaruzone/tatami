using System.Text.Json;
using Tatami.Services;

internal static partial class TatamiCli
{
    private static async Task<int?> RunConfigCommandAsync(CliArguments arguments)
    {
        var positional = arguments.Positional;
        var positionalCount = positional.Count;
        var configPath = arguments.ConfigPath;
        if (positionalCount == 2 && positional[0] == "config" && positional[1] == "show")
        {
            var config = await ResolveConfigAsync(configPath);
            Console.WriteLine(JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (positionalCount == 1 && positional[0] == "doctor")
            return await RunDoctorAsync(configPath, arguments.Fix);
        if (positionalCount == 4 && positional[0] == "config" && positional[1] == "set")
        {
            var config = await ResolveConfigAsync(configPath);
            var key = positional[2];
            var value = positional[3];
            config = key switch
            {
                "input" => config with { InputPath = value },
                "output" => config with { OutputPath = value },
                "builder" when value is "builtin" or "external" => config with { Builder = value },
                "external.project" => config with { External = config.External with { ProjectPath = value } },
                "external.executable" => config with { External = config.External with { Executable = value } },
                "external.arguments" => config with { External = config.External with { Arguments = JsonSerializer.Deserialize<string[]>(value) ?? [] } },
                "external.timeout" when int.TryParse(value, out var seconds) => config with { External = config.External with { TimeoutSeconds = seconds } },
                _ => null!
            };
            if (config is null) return UsageError($"不明または不正な設定項目です: {key}");
            if (configPath is not null)
            {
                var target = Path.GetFullPath(configPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await config.SaveAsync(target);
            }
            else await new WorkspaceSettings().SaveAsync(config);
            if (!_quiet) Console.WriteLine($"設定を更新しました: {key}");
            return 0;
        }
        return null;
    }
}
