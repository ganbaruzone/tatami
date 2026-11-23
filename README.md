# Tatami

Tatami は、Markdown のメモを編集し、公開するメモを静的 HTML に変換するデスクトップアプリです。メモは通常の `.md` ファイルとして保存され、生成したサイトも一般的な HTML と CSS で構成されます。

## できること

- Markdown の編集、検索、タブ表示、未保存内容の自動退避
- ノート間リンクの挿入と、リンクをクリックして別ノートを開く操作
- `publish: true` のノートだけを HTML に出力
- ノート全体または `:::private` で囲んだ段落を非公開にする設定
- 画像や添付ファイルの取り込み、内蔵または外部ビルダーでのサイト生成
- GUI を使わない CLI でのノート確認、検索、検証、ビルド

## 起動する

.NET 10 SDK をインストールし、リポジトリのルートで実行します。

```sh
dotnet run --project Tatami.csproj
```

メモのフォルダと HTML の出力先をそれぞれ選びます。メモを編集し、公開したいノートに `publish: true` を設定してから「サイトをビルド」を押してください。詳しい手順は[利用ガイド](docs/usage.md)を参照してください。

## 公開するノート

Markdown ファイルの先頭に YAML Front Matter を置きます。

```markdown
---
title: はじめての記事
publish: true
slug: first-post
---

# こんにちは

このノートがサイトに出力されます。
```

`publish: true` を指定していないノートは公開されません。公開ノート内の一部だけを除外するには、対象の段落を `:::private` と `:::` で囲みます。Markdown の書き方やリンク、添付ファイルについては[利用ガイド](docs/usage.md)を参照してください。

## CLI

CLI は GUI を起動せずにノートを調べたり、サイトをビルドしたりできます。

```sh
dotnet run --project Tatami.Cli -- --help
dotnet run --project Tatami.Cli -- notes list --input ./notes
dotnet run --project Tatami.Cli -- build --input ./notes --output ./site-output
```

コマンドの一覧や設定方法は[利用ガイド](docs/usage.md)の「CLI を使う」章を参照してください。

## Git とファイル

メモのフォルダと HTML 出力先は別々に設定できます。どちらも Git で管理できます。Tatami は Git のコミットやステージを行いません。生成 HTML はビルド時に更新されるため、変更を `git diff` で確認してからコミットできます。

ノートをゴミ箱へ移動・復元する操作はファイルシステム上で行います。Git 管理下のノートを移動すると、Git には元ファイルの削除として表示されます。ゴミ箱の内容は Git の追跡対象になりません。

## 開発

テストを実行するには .NET 10 SDK が必要です。

```sh
dotnet restore Tests/Tatami.Tests.csproj
dotnet build Tatami.csproj
dotnet test Tests/Tatami.Tests.csproj
```

CLI のビルドと発行方法、外部ビルダー、プレビュー環境の要件は[利用ガイド](docs/usage.md)に記載しています。
