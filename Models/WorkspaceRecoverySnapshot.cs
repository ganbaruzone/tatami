using System;

namespace Tatami.Models;

public sealed record RecoveredNoteTab(
    string Path,
    string Text,
    string BaselineHash,
    bool WasDirty,
    int CaretOffset,
    bool HasPendingMetadata = false,
    string? PendingTitle = null,
    string? PendingSlug = null,
    bool PendingPublish = false);

public sealed record WorkspaceRecoverySnapshot(
    int Version,
    string WorkspacePath,
    DateTimeOffset SavedAt,
    int ActiveTabIndex,
    RecoveredNoteTab[] Tabs);
