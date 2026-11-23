---
title: Tatamiの使い方
date: 2026-11-22
tags: [Tatami, Markdown, ガイド]
publish: true
slug: tatami-guide
---

# Tatami 利用ガイド

Tatami は Markdown のメモを編集し、公開対象のノートから静的サイトを作るデスクトップアプリです。このページでは、保存場所の設定から編集、公開、ビルドまでを順に説明します。

## 1. 起動して保存場所を選ぶ

.NET 10 SDK を用意し、リポジトリのルートで起動します。

```sh
dotnet run --project Tatami.csproj
```

左側の「メモのディレクトリ…」で Markdown を置くフォルダを、「HTML出力先…」で生成したサイトを置くフォルダを選びます。初回は空の出力フォルダを使うと管理しやすくなります。

```text
workspace/
├── notes/          メモ
└── site-output/    ビルドした HTML と CSS
```

2つの場所は独立して保存され、次回起動時にも復元されます。設定欄にはディレクトリ名が表示され、フルパスはツールチップで確認できます。保存先の設定は、OS のユーザー別アプリデータ領域にある `Tatami/workspace.json` に保存されます。

メモと出力先はそれぞれ Git で管理できます。Tatami はステージやコミットを行わないので、ビルド後に `git status` や `git diff` で変更を確認してください。出力先にはメモフォルダそのものや、その親フォルダを指定できません。

## 2. ノートを作成して編集する

左の一覧から `.md` ファイルを選ぶと編集できます。「＋ 新規」から現在のフォルダにノートを作成できます。新規ノートは非公開で始まります。同じ名前のファイルやフォルダがある場合は、`あ2.md`、`あ3.md` のように空いている番号が付きます。

| 操作 | macOS | Windows / Linux |
| --- | --- | --- |
| 保存 | ⌘S | Ctrl+S |
| ファイル名で開く | ⌘P | Ctrl+P |
| 検索 | ⌘F | Ctrl+F |
| ノートリンクを挿入 | ⌘K | Ctrl+K |
| ノート本文を検索 | ⌘⇧F | Ctrl+Shift+F |
| 元に戻す | ⌘Z | Ctrl+Z |

未保存の変更はタブとウィンドウタイトルの「●」で示されます。タブを切り替えても編集内容と Undo 履歴は保たれます。未保存タブを閉じるときは保存・破棄・キャンセルを選べます。メモフォルダの変更時やアプリ終了時は、未保存タブをまとめて確認します。

未保存の編集内容は約5秒ごとに自動退避され、次回起動時に復元できます。別のアプリで編集中のファイルが変更された場合は、Tatami はそのファイルを無断で上書きしません。

### ノートの整理

- 「名前変更」は編集中ノートの名前を変えます。既存ファイルは上書きしません。
- 「ゴミ箱へ」はノートをメモフォルダ内の `.tatami-trash/` に移動します。
- 「ゴミ箱から復元」では移動前の場所へ戻します。そこに同名ファイルがある場合は、先に既存ファイルの名前を変えてください。
- ファイルの移動は Tatami が行います。Git 管理されたノートをゴミ箱へ移すと、Git には元ファイルの削除として表示されます。ゴミ箱の中身はリポジトリのローカル除外設定に追加され、追跡対象になりません。復元したノートの内容が変わっていなければ、Git の削除表示も解消します。

## 3. ノート間リンクと添付

「リンク挿入」または ⌘K／Ctrl+K でノートや見出しを選ぶと、標準 Markdown の相対リンクを挿入できます。新しいリンク先ノートも作成できます。

編集中の Markdown リンクは、macOS では ⌘クリック、Windows / Linux では Ctrl+クリックで開けます。ワークスペース内のノートはエディターのタブで、Web サイトへのリンクはシステムの既定ブラウザーで開きます。通常クリックでリンクをたどるのではなく、修飾キーを押しながらクリックしてください。

「被リンク・言及」では、現在のノートへのリンクやタイトルの言及を確認できます。「リンク切れ」では、存在しないノートや見出しへのリンクを一覧できます。ノートの名前変更や移動では、ワークスペース内の相対リンクも更新されます。

「画像・添付ファイルを取り込む」から画像、PDF、音声、動画を選ぶと、`.tatami-assets/` にコピーされ、ノートへ相対リンクが挿入されます。対応形式は PNG / JPEG / GIF / WebP / AVIF / BMP、PDF、MP3 / WAV / OGG、MP4 / WebM で、1ファイル100 MBまでです。添付は Git で管理できます。

ビルド時に出力されるのは、公開ノートの公開部分から参照されている添付だけです。非公開ノートや非公開ブロックだけで参照される添付、未参照の添付は出力されません。存在しない添付が参照されている場合、ビルドは失敗し、前回のサイト出力を保ちます。

## 4. 公開するノートを指定する

ノートの先頭に YAML Front Matter を記述します。

```markdown
---
title: はじめての記事
date: 2026-11-23
tags: [日記, Tatami]
publish: true
slug: first-post
---

# 今日のこと

この段落は公開されます。
```

`publish: true` のノートだけが公開されます。指定がない場合、`false` の場合、Front Matter 自体がない場合は非公開です。編集画面上部の公開チェック、タイトル、slug から設定でき、「適用」を押すと Front Matter に反映されます。保留中の設定も保存またはビルド時に反映されます。

- `title` を省略すると、ファイル名がタイトルになります。
- `slug` を省略すると、ファイル名を元に URL が作られます。日本語、数字、`-`、`_` を使用できます。
- 同じ slug や不正な slug がある場合、ビルドはエラーになります。
- 内蔵ビルダーは記事一覧、タグ別ページ、年別アーカイブも生成します。

### 一部の段落を非公開にする

公開ノートの一部分を出力しない場合は、段落を `:::private` と `:::` で囲みます。

```markdown
公開する文章です。

:::private
この文章は Markdown に残りますが、生成 HTML には含まれません。
:::

この文章も公開されます。
```

囲みは独立した行に置きます。閉じる `:::` がない場合はビルドエラーです。コードブロック内の記法例は非公開指定として扱われません。ノート全体が非公開の場合、この指定に関係なくノートは出力されません。

編集行の先頭で `/` を入力すると挿入メニューが開きます。検索して候補を選ぶと、Mermaid 図、非公開エリア、画像取り込み、表、コードブロック、見出しなどのひな形を挿入できます。矢印キーと Enter で選び、Escape で閉じます。

## 5. ビルドしてプレビューする

1. メモと HTML 出力先を確認します。
2. 「サイトをビルド」を押します。
3. Tatami が変更済みの編集タブを保存し、公開ノートから HTML を生成します。
4. 成功するとアプリ内プレビューに切り替わります。

プレビューに表示されるのは最後にビルドした HTML です。Markdown の変更を反映するには再ビルドしてください。プレビューの更新ボタンは保存済み HTML を読み直す操作で、ビルドは行いません。生成した HTML は一般的なブラウザーでも確認できます。

ビルドでは生成 HTML を新しい内容で上書きします。出力先全体を消去することはなく、`.tatami-manifest.json` に記録した生成物を管理します。公開解除、ノート削除、slug 変更で不要になった HTML は、次の成功したビルドで削除されます。生成物以外のファイルと衝突する場合は停止します。

失敗したビルドでは、前回の公開 HTML が残ることがあります。公開を取り消した場合はビルド成功を確認してください。強制終了や電源断を含む、サイト全体の完全なトランザクションは保証されません。同じ出力先を複数のアプリや外部ツールから同時に変更しないでください。

生成 HTML を Git 管理する場合は、出力先リポジトリの除外設定を確認してください。リポジトリ直下の `site-output/` はこのリポジトリの `.gitignore` 対象です。

## 6. CLI を使う

CLI は GUI を起動せず、ノートの確認・検索・検証やサイト生成を実行します。リポジトリのルートで次のように使います。

```sh
dotnet run --project Tatami.Cli -- --help
dotnet run --project Tatami.Cli -- notes list --input ./notes
dotnet run --project Tatami.Cli -- notes validate --input ./notes
dotnet run --project Tatami.Cli -- notes search "静的サイト" --input ./notes
dotnet run --project Tatami.Cli -- build --input ./notes --output ./site-output --json
dotnet run --project Tatami.Cli -- preview --output ./site-output
```

CLI を発行すると、`tatami` コマンドとして実行できます。

```sh
dotnet publish Tatami.Cli/Tatami.Cli.csproj -c Release -o ./artifacts/tatami
./artifacts/tatami/tatami --help
```

発行物は framework-dependent です。実行する環境に .NET 10 ランタイムが必要です。

| コマンド | 内容 |
| --- | --- |
| `tatami build` | 内蔵または設定済み外部ビルダーで生成。`--input`、`--output`、`--config`、`--json` を指定できます。 |
| `tatami build --watch` | 初回ビルド後、Markdown の変更を検出して再ビルドします。Ctrl+C で終了します。 |
| `tatami preview` | 生成済み HTML を `127.0.0.1` で配信します。Ctrl+C で終了します。 |
| `tatami notes list` / `show <path>` | ノート一覧、Front Matter、公開状態を表示します。 |
| `tatami notes validate` | 出力先を変更せず、Front Matter、公開 slug の重複、非公開ブロックを検査します。 |
| `tatami notes search <query>` | ファイル名、タイトル、タグ、本文を検索します。 |
| `tatami notes create <name>` | 非公開ノートを作成します。同名項目があれば番号を付けます。 |
| `tatami notes rename <path> <name>` | ノート名を変更します。 |
| `tatami notes metadata set <path> <key> <value>` | `title`、`publish`、`slug` を変更します。 |
| `tatami notes trash <path>` / `trash list` / `restore <id>` | ノートをゴミ箱へ移動し、一覧表示または復元します。 |
| `tatami config show` / `set <key> <value>` | CLI の設定を確認・変更します。 |
| `tatami doctor` | SDK、設定、パス、外部ビルダーを診断します。 |

入力・出力の相対パスはコマンドを実行したディレクトリを基準にします。パスを省略すると `--config <file>` またはアプリの設定を使います。`build --json` は結果を JSON で出力します。成功時は終了コード 0、実行時エラーは 1、引数エラーは 2 です。

## 7. 外部ビルダー

「ビルダー設定…」から内蔵ビルダーまたは外部コマンドを選択できます。外部ビルダーのインストールとプロジェクトの用意は別途必要です。

外部コマンドには実行ファイル、引数（1行に1つ）、作業ディレクトリとなるプロジェクト、タイムアウトを設定します。例えば Node.js プロジェクトで `build` スクリプトを実行する場合は、実行ファイルに `node`、引数に `--run` と `build` を設定します。

外部コマンドには次の環境変数が渡されます。

| 環境変数 | 内容 |
| --- | --- |
| `TATAMI_CONTENT_DIR` | 公開ノートだけを含む一時 Markdown フォルダ |
| `TATAMI_OUTPUT_DIR` | HTML などを書き出す一時出力フォルダ |

引数の `{content}` と `{output}` は各フォルダのパスに置き換わります。外部ツールは一時出力先へ HTML を生成してください。終了コードが 0 で成果物がある場合だけ、Tatami が最終出力先へ反映します。失敗、中止、タイムアウトの場合は前回の公開出力を保持します。

CLI で設定する例です。

```sh
tatami config set builder external
tatami config set external.project ./my-site
tatami config set external.executable node
tatami config set external.arguments '["--run","build"]'
tatami config set external.timeout 600
tatami doctor
tatami build
```

`tatami doctor` は SDK、設定、パス、外部ビルダーの実行ファイルやプロジェクトを確認します。`tatami doctor --fix` は安全に判断できる不足ディレクトリを作成し、依存関係の導入コマンドを表示します。依存関係のインストールは実行しません。

外部コマンドはユーザーの権限で実行されます。信頼できるプロジェクトを指定してください。Tatami は外部コマンドをサンドボックス化せず、HTML の安全性も外部ビルダーに委ねます。

## 補足

- 内蔵プレビューは OS の WebView を使います。Windows では WebView2、Linux では対応する WebKit 環境が必要です。macOS では標準の WebKit を使います。
- プレビューは `127.0.0.1` 上で配信されます。外部ネットワークには公開されません。
- Markdown のリンクは通常の相対リンクとして保存され、サイト出力時に公開ノート向けの URL に変換されます。
- Tatami は生 HTML を無効化し、標準テンプレートに Content Security Policy を設定します。外部ビルダーを使う場合は、そのツールとテンプレートの安全性を確認してください。
