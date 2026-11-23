using System;

namespace Tatami.Models;

public sealed record SavedNoteView(
    string Name,
    string? Tag = null,
    string PublishState = "any",
    DateOnly? FromDate = null,
    DateOnly? ThroughDate = null,
    string? Folder = null,
    string SortBy = "date",
    bool Descending = true);

public sealed record SavedNoteViewResult(string Path, string Display);
