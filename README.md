# Tatami

C# / .NET 10 / Avalonia 12.1.5 のMarkdownノートアプリ。Markdown編集画面と静的サイトビルダーMVPを備えています。

## 起動とテスト

.NET 10 SDKが必要です。リポジトリのルートで実行します。

```sh
dotnet restore Tests/Tatami.Tests.csproj
dotnet build Tatami.csproj
dotnet test Tests/Tatami.Tests.csproj
dotnet run --project Tatami.csproj
```

## CLI（初期版）

Avaloniaの画面を起動せずにノートを確認できます。リポジトリのルートで `--project Tatami.Cli` を指定します。

```sh
dotnet run --project Tatami.Cli -- --help
dotnet run --project Tatami.Cli -- notes list --input ./notes
dotnet run --project Tatami.Cli -- notes show welcome.md --input ./notes
dotnet run --project Tatami.Cli -- notes create welcome --input ./notes
dotnet run --project Tatami.Cli -- notes rename welcome.md greeting --input ./notes
dotnet run --project Tatami.Cli -- notes trash greeting.md --input ./notes
dotnet run --project Tatami.Cli -- notes trash list --input ./notes
dotnet run --project Tatami.Cli -- notes restore 00000000-0000-0000-0000-000000000000 --input ./notes
dotnet run --project Tatami.Cli -- config set input ./notes
dotnet run --project Tatami.Cli -- config show
dotnet run --project Tatami.Cli -- doctor
dotnet run --project Tatami.Cli -- build --input ./notes --output ./site-output --json
```

`notes list` は公開状態と相対パスを表示し、`notes show` はFront Matterと公開判定を表示します。`notes create` は `publish: false` の新規ノートを作成し、`--directory` で入力内の作成先を選べます。`notes rename` は入力内のノート名を変更し、同名ファイルを上書きしません。`notes trash <path>` はノートをTatamiの `.tatami-trash/` に移動します。`notes trash list` で一覧を表示し、表示されたIDを `notes restore <id>` に渡すと元の場所に戻せます。CLIからゴミ箱を完全削除する機能はありません。`config show` は設定JSONを表示し、`config set <key> <value>` は `input`、`output`、`builder`、`external.project`、`external.executable`、`external.arguments`（JSON文字列配列）、`external.timeout`（秒）を更新します。`--config <file>` を指定すると任意の設定ファイルを読み書きします。`doctor` は.NET SDK、設定、入力・出力パス、外部ビルダーの実行ファイルとプロジェクトを診断し、問題があれば終了コード1を返します。`build` は内蔵または設定済み外部ビルダーを使います。入力・出力の省略時は `--config <file>` またはアプリのユーザー設定を参照します。`--json` は件数・出力先・成否をJSONで出します。成功は終了コード0、実行時エラーは1、引数エラーは2です。

## 使い方

1. Markdownの先頭に次のFront Matterを記述して保存します。
2. Tatamiの「フォルダを開く」でノートの保存先を選択します。
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

- `.md`（拡張子の大文字・小文字は不問）を再帰的に読み取ります。選択した出力先、`.git`、シンボリックリンクは探索しません。エクスプローラーもMarkdownを階層表示します。
- `publish: true` のみ公開します。未指定・Front Matterなし・`false` は非公開です。入力ファイルを書き換えません。
- `date` は任意の `yyyy-MM-dd`、`tags` は配列です。未知のメタデータは無視します。不正なYAML・日付・重複キー・閉じられていないFront Matterはビルドエラーです。
- `title` 未指定時は拡張子を除くファイル名を使います。
- `slug` 未指定時はファイル名の空白をハイフンに置換します。文字・数字・`_`・`-` のみ100文字以内で、日本語も使えます。Windows予約名は拒否します。明示的な空slugはエラーです。
- 同一slugは大文字・小文字やUnicode正規化を考慮して重複エラーにします。既存slugの大文字・小文字だけを変更する場合も、安全のためエラーにします。別のslugまたは新しい出力先を使ってください。
- 出力は `{slug}/index.html` と `style.css`。CSSへの相対パスは `../style.css` なので直接HTMLを開けます。
- リポジトリ直下の `site-output/` は `.gitignore` 対象です。別の出力先を使う場合は、そのパスもGitの除外設定に追加してください。

## ノートの作成・名前変更・ゴミ箱

- 左側の「＋ 新規」（⌘N / Ctrl+N）で、選択フォルダにMarkdownを作成します。ファイル選択中はその親フォルダに作成します。新規ノートは `publish: false` です。
- 「名前変更」は編集中のファイル名を変更します。未保存の本文とUndo履歴を保持し、同名ファイルは上書きしません。
- 「ゴミ箱へ」で編集中のノートを移動します。未保存の場合は保存・破棄・キャンセルを選びます。
- 「復元」から元の場所に戻せます。同名ファイルがある場合は上書きせず中止するので、既存ファイルを名前変更してから復元してください。

ゴミ箱はOSのゴミ箱とは別の、メモフォルダ内の `.tatami-trash/` です。一覧とビルド対象から除外します。完全削除機能はありません。リポジトリのGit除外設定に追加済みですが、別のGit管理メモフォルダでも `.tatami-trash/` を除外してください。公開済みHTMLから記事を取り除くには、移動後に再ビルドを成功させてください。

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

`SiteConfig(InputPath, OutputPath)` を渡すオーバーロードと、System.Text.Jsonによる `SaveAsync` / `LoadAsync` もあります。設定のパスは絶対パスを推奨します。相対パスはプロセスの作業ディレクトリ基準です。UIではメモとHTML出力先を別々に選択し、ユーザー別アプリデータ領域の `Tatami/workspace.json` に保存・復元します。

解析は `NoteRepository`、公開判定は `PublishFilter`、Markdownとテンプレート変換は `TemplateRenderer`、パス検査は `PathSafety`、更新と復元は `SiteBuilder` に分離しました。ビルダー本体はAvaloniaへの依存がありません（同一プロジェクトに配置）。

## パッケージ

既存のMarkdig 1.6.0、YamlDotNet 18.1.0を再利用し、アプリにはScriban 7.6.1と、HTMLプレビュー用のAvalonia.Controls.WebView 12.1.2を追加しています。テスト用にMicrosoft.NET.Test.Sdk 17.14.1、xunit 2.9.3、xunit.runner.visualstudio 3.1.4を追加しました。既存のAvalonia関連パッケージとCommunityToolkit.Mvvmは変更していません。

## 今回の対象外

記事一覧、タグ別ページ、月別アーカイブ、カレンダー、RSS、sitemap、Obsidian内部リンク、バックリンク、差分ビルド、複数テーマ、Git push、Cloudflare Pagesデプロイ。将来の拡張では、添付ファイルの公開許可、URL変換、生成物の所有権管理、テンプレートのエスケープ境界を維持してください。

## Markdown編集画面

VS Codeを参考にしたダークテーマで、左に階層型エクスプローラー、右に編集バッファ、下にエラーと処理ログを配置しています。境界をドラッグしてペインをリサイズできます。

- Markdownを選択して直接編集。行番号、簡易Markdown色分け、折り返し、Undo/Redo、検索に対応します。
- 保存ボタンまたはmacOSの⌘S／Windows・LinuxのCtrl+Sで保存します。検索は⌘F／Ctrl+Fです。
- 未保存の変更はタブとタイトルの `●` で表示します。ファイル・フォルダの切り替えとウィンドウ終了時は「保存して続行／変更を破棄／キャンセル」を選択できます。
- 編集バッファは1ファイルです。複数タブは未実装です。HTMLプレビューはビルド済みの成果物を表示し、ライブ変換は行いません。
- ビルドは現在のバッファを保存してから実行します。保存が失敗した場合はビルドしません。
- 外部変更や削除を検出した場合は上書きせず、下のパネルにエラーを表示します。編集中の内容をコピーして退避してから、別ファイルへ切り替えて再度開いてください。
- UTF-8（BOMあり・なし）とBOMで識別できるUnicode形式を読み書きし、元の改行を保持します。Shift-JIS等の自動判別は行いません。
- 一覧の更新ボタンはディスク上の一覧だけを再取得し、編集中のバッファは保持します。自動ファイル監視はありません。
- 保存は同じフォルダ内の一時ファイルから置換します。外部変更チェック後の短い競合窓はあるため、同一ファイルへの同時保存は避けてください。

## HTMLプレビュー・非公開段落

現時点の詳しい操作ガイドは [docs/usage.md](docs/usage.md) を参照してください。このMarkdown自体も `publish: true` でビルドできます。

メモとHTML出力先は左側の別々のボタンで設定します。以後のビルドでは保存済みの出力先を使用し、成功するとアプリ内プレビューに切り替わります。「編集 / HTML」で切り替え、記事選択欄で生成済みの記事を閲覧します。プレビューには公式のAvalonia.Controls.WebViewを使用します。macOSはWebKit、WindowsはWebView2、Linuxは対応するWebKit環境が必要です。

公開ノートの文章は通常すべて出力されます。非公開段落は独立した行の `:::private` と `:::` で囲みます。元のMarkdownを変更せず、Markdigで境界を認識して、HTML生成前に囲み内のソースを取り除きます。脚注や参照リンクの定義も除外対象です。閉じ忘れはビルドエラー、コードブロック内の記法例はそのまま表示します。ノート全体の `publish: true` 条件は維持しています。

## 外部ビルダー

「ビルダー設定…」で内蔵／外部コマンドを切り替えます。Astroをはじめ、公開用Markdownの入力と静的HTMLの出力に対応する任意のコマンドを接続できます。[Astroサンプル](examples/astro/README.md) と [接続・操作ガイド](docs/usage.md) を参照してください。

`BuildCoordinator.BuildAsync(SiteConfig, IProgress<string>?, CancellationToken)` が選択したビルダーを実行します。既存の `SiteBuilder` は内蔵HTML生成用です。外部ツールには公開判定・非公開段落除去後の一時Markdownを環境変数 `TATAMI_CONTENT_DIR` で渡し、生成先は `TATAMI_OUTPUT_DIR` とします。ユーザーが設定したコマンドを直接実行し、標準出力／標準エラー・終了コード・中止・タイムアウトを扱います。

生成物反映は `GeneratedSiteWriter` を内蔵／外部で共用します。外部ツールのCSS・JS・画像やルートのindex.htmlも管理します。HTMLプレビューはASP.NET CoreのループバックHTTPサーバーを使い、生成物一覧に記録されたファイルだけを配信します。これに伴い `Microsoft.AspNetCore.App` フレームワーク参照を追加しました（新しいNuGetパッケージは不要）。SDKなしの環境ではASP.NET Core 10ランタイムも必要です。
