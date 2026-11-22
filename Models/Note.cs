using System;

namespace Tatami.Models;

public sealed record Note(string SourcePath, NoteMetadata Metadata, string Markdown, string? FrontMatter = null);

public sealed class NoteMetadata
{
    public string? Title { get; set; }
    public DateOnly? Date { get; set; }
    public string[] Tags { get; set; } = [];
    public bool Publish { get; set; }
    public string? Slug { get; set; }
}

public sealed record BuildResult(int PublishedCount, int PrivateCount, string OutputPath);
