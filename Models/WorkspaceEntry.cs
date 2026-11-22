using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tatami.Models;

public sealed record WorkspaceEntry(string Name, string FullPath, bool IsDirectory, IReadOnlyList<WorkspaceEntry> Children)
{
    public bool IsExpanded { get; set; }
    public string Icon => IsDirectory ? "▸" : "M↓";
    public static IReadOnlyList<WorkspaceEntry> Read(string path) => new DirectoryInfo(path)
        .EnumerateFileSystemInfos()
        .Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0)
        .Where(info => info is DirectoryInfo ? info.Name is not (".git" or "bin" or "obj" or "node_modules" or ".astro" or ".tatami-trash")
            && !File.Exists(System.IO.Path.Combine(info.FullName, ".tatami-manifest.json"))
            : info.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(info => info is DirectoryInfo).ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
        .Select(info => new WorkspaceEntry(info.Name, info.FullName, info is DirectoryInfo,
            info is DirectoryInfo ? Read(info.FullName) : [])).ToArray();
}
