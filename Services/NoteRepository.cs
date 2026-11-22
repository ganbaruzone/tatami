using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tatami.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Tatami.Services;

public sealed class NoteRepository
{
    private readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithDuplicateKeyChecking()
        .WithTypeConverter(new DateOnlyConverter())
        .Build();

    public IEnumerable<Note> ReadAll(string inputPath, string outputPath, params string[] excludedPaths)
    {
        if (Path.GetFullPath(inputPath).Split(Path.DirectorySeparatorChar)
            .Any(part => part.Equals(NoteFileService.TrashFolderName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Tatamiのゴミ箱はビルド対象にできません。");
        foreach (var file in Enumerate(inputPath, outputPath, excludedPaths))
        {
            Note note;
            try { note = Parse(file, File.ReadAllText(file)); }
            catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or FormatException or InvalidDataException)
            { throw new InvalidDataException($"Front Matterを読み込めません: {file}: {ex.Message}", ex); }
            yield return note;
        }
    }

    private static IEnumerable<string> Enumerate(string directory, string outputPath, string[] excludedPaths)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (PathSafety.IsWithin(entry, outputPath) || System.Linq.Enumerable.Any(excludedPaths, root => PathSafety.IsWithin(entry, root))) continue;
            var attributes = File.GetAttributes(entry);
            // Do not follow links to files outside the selected workspace, or recursive links.
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (Path.GetFileName(entry) is ".git" or "node_modules" or ".astro" or ".tatami-trash") continue;
                foreach (var child in Enumerate(entry, outputPath, excludedPaths)) yield return child;
            }
            else if (string.Equals(Path.GetExtension(entry), ".md", StringComparison.OrdinalIgnoreCase))
                yield return entry;
        }
    }

    public Note Parse(string path, string text)
    {
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        if (reader.ReadLine()?.Trim() != "---") return new Note(path, new NoteMetadata(), text);
        var yaml = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Trim() is "---" or "...")
                return new Note(path, _yaml.Deserialize<NoteMetadata>(yaml.ToString()) ?? new NoteMetadata(), reader.ReadToEnd(), yaml.ToString());
            yaml.AppendLine(line);
        }
        throw new InvalidDataException("Front Matterの終端がありません。");
    }

    private sealed class DateOnlyConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(DateOnly);
        public object ReadYaml(YamlDotNet.Core.IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var scalar = YamlDotNet.Core.ParserExtensions.Consume<YamlDotNet.Core.Events.Scalar>(parser);
            return DateOnly.ParseExact(scalar.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        public void WriteYaml(YamlDotNet.Core.IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException();
    }
}
