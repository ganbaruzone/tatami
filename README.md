# Tatami

C# / .NET 10 / Avalonia 12.1.5 のMarkdownノートアプリ。既存のファイル一覧・読み取り専用表示に静的サイトビルダーMVPを追加しています。ノートの編集・保存は現時点では外部エディタで行ってください。

## 起動とテスト

.NET 10 SDKが必要です。リポジトリのルートで実行します。

```sh
dotnet restore Tests/Tatami.Tests.csproj
dotnet build Tatami.csproj
dotnet test Tests/Tatami.Tests.csproj
dotnet run --project Tatami.csproj
```

## 使い方

1. Markdownの先頭に次のFront Matterを記述して保存します。
2. Tatamiの「フォルダを選択」でノートの保存先を選択します。
3. 「サイトをビルド」を押し、出力フォルダを選択します。入力未選択の場合は入力フォルダから選択します。
4. 完了メッセージを確認し、出力された `hello/index.html` をブラウザで開きます。

```markdown
---
title: はじめての記事
date: 2026-11-22
tags:
  - CSharp
  - SSG
publish: true
slug: hello
---

# 本文

**こんにちは**。Markdownのテーブルやコードブロックも使えます。
```

- `.md`（拡張子の大文字・小文字は不問）を再帰的に読み取ります。選択した出力先、`.git`、シンボリックリンクは探索しません。既存画面のファイル一覧は従来どおり直下のファイルを表示します。
- `publish: true` のみ公開します。未指定・Front Matterなし・`false` は非公開です。入力ファイルを書き換えません。
- `date` は任意の `yyyy-MM-dd`、`tags` は配列です。未知のメタデータは無視します。不正なYAML・日付・重複キー・閉じられていないFront Matterはビルドエラーです。
- `title` 未指定時は拡張子を除くファイル名を使います。
- `slug` 未指定時はファイル名の空白をハイフンに置換します。文字・数字・`_`・`-` のみ100文字以内で、日本語も使えます。Windows予約名は拒否します。明示的な空slugはエラーです。
- 同一slugは大文字・小文字やUnicode正規化を考慮して重複エラーにします。既存slugの大文字・小文字だけを変更する場合も、安全のためエラーにします。別のslugまたは新しい出力先を使ってください。
- 出力は `{slug}/index.html` と `style.css`。CSSへの相対パスは `../style.css` なので直接HTMLを開けます。
- リポジトリ直下の `site-output/` は `.gitignore` 対象です。別の出力先を使う場合は、そのパスもGitの除外設定に追加してください。

## 出力の保護

`.tatami-manifest.json` に生成ファイルとSHA-256ハッシュを記録します。このファイルを保持してください。初回は空の出力先を推奨します。

全ノートの解析・HTML生成・重複検査・既存ファイルの確認が成功してから書き込みます。既存の出力フォルダ全体は削除しません。前回生成したファイルだけを更新し、非公開化・削除・slug変更で不要になった生成HTMLも削除します。空の記事フォルダは残る場合があります。管理対象外のファイルは保持し、衝突する場合や生成物が手動編集されている場合は上書きせず中止します。

ファイル単位の一時ファイル置換を使い、通常の書き込み例外では変更済みファイルを元に戻します。強制終了・電源断・復元自体のI/O障害に対するサイト全体の完全なトランザクションではありません。復元失敗はエラー表示されます。大量のサイトでは生成HTMLと復元用データをメモリに保持するため、将来ディスクステージングへの拡張が必要です。同じ出力先を複数のプロセスや外部ツールから同時に変更しないでください。

非公開化のビルドに失敗した場合は以前の公開済みHTMLが残ります。公開先への反映前に必ず成功を確認してください。入力と同一・入力の親ディレクトリへの出力、出力パス内のシンボリックリンクは拒否します。

## HTMLとテンプレート

`Templates/default.html` と `Templates/style.css` はビルド／publish時にアプリとともにコピーします。Scribanへ `title`、`date`、`tags`、`content` を渡します。通常の文字列はC#側で一度だけHTMLエスケープし、テンプレートで再エスケープしません。`content` はMarkdigで生成したHTMLです。

Markdigの `UseAdvancedExtensions()` を基に、生HTML、任意属性、メディア埋め込みを無効化しています。実行可能スキームやファイルURLも抑制し、標準テンプレートにはスクリプト実行を禁止するCSPを設定しています。テンプレート自体は信頼するローカルファイルとして扱います。外部から取得したテンプレートをそのまま実行しないでください。

Markdownの画像・リンク記法はHTMLになりますが、ローカル画像など添付ファイルのコピーや相対リンクの書き換えは行いません。外部画像URLを使うか、公開したい画像を別途適切な相対位置へ配置してください。フォルダ内の添付ファイルを一括コピーしないことで、非公開ファイルの流出を避けています。

## UIから独立したAPI

```csharp
var result = await new Tatami.Services.SiteBuilder().BuildAsync(inputPath, outputPath);
```

`SiteConfig(InputPath, OutputPath)` を渡すオーバーロードと、System.Text.Jsonによる `SaveAsync` / `LoadAsync` もあります。設定のパスは絶対パスを推奨します。相対パスはプロセスの作業ディレクトリ基準です。UIでの設定自動保存は今回の対象外です。

解析は `NoteRepository`、公開判定は `PublishFilter`、Markdownとテンプレート変換は `TemplateRenderer`、パス検査は `PathSafety`、更新と復元は `SiteBuilder` に分離しました。Avaloniaへの依存はありません（同一プロジェクトに配置）。

## パッケージ

既存のMarkdig 1.6.0、YamlDotNet 18.1.0を再利用し、アプリにはScriban 7.6.1だけを追加しています。テスト用にMicrosoft.NET.Test.Sdk 17.14.1、xunit 2.9.3、xunit.runner.visualstudio 3.1.4を追加しました。既存のAvalonia関連パッケージとCommunityToolkit.Mvvmは変更していません。

## 今回の対象外

記事一覧、タグ別ページ、月別アーカイブ、カレンダー、RSS、sitemap、Obsidian内部リンク、バックリンク、差分ビルド、複数テーマ、Git push、Cloudflare Pagesデプロイ。将来の拡張では、添付ファイルの公開許可、URL変換、生成物の所有権管理、テンプレートのエスケープ境界を維持してください。
