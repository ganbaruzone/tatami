using System;
using System.Collections.Generic;
using System.Linq;

namespace Tatami.Services;

public sealed record SlashCommand(string Id, string Title, string Description, string Keywords, string Insertion, string? Placeholder = null);

public sealed class SlashCommandService
{
    private static readonly SlashCommand[] AvailableCommands =
    [
        new("diagram", "図を挿入", "Mermaidの図を編集できるひな形", "diagram mermaid flowchart chart", "```mermaid\nflowchart TD\n    A[開始] --> B[処理]\n```\n", "開始"),
        new("private", "非公開エリアを挿入", "公開ビルドから除外する範囲", "private privacy hidden 非公開", ":::private\n非公開の内容\n:::\n", "非公開の内容"),
        new("image", "画像を取り込む", "画像を管理領域へコピーしてリンクを挿入", "image picture photo attachment 画像", ""),
        new("table", "表を挿入", "Markdownの2列テーブル", "table grid 表", "| 項目 | 内容 |\n| --- | --- |\n|  | セル内容 |\n", "セル内容"),
        new("code", "コードブロックを挿入", "言語指定付きコードブロック", "code codeblock fence", "```言語\n\n```\n", "言語"),
        new("heading", "見出しを挿入", "レベル2の見出し", "heading title 見出し", "## 見出し\n\n", "見出し")
    ];

    public IReadOnlyList<SlashCommand> Commands => AvailableCommands;

    public IReadOnlyList<SlashCommand> Filter(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return AvailableCommands;
        return AvailableCommands.Where(command =>
            command.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || command.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || command.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
