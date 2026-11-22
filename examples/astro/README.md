# Tatami × Astro

Tatamiの外部ビルダー連携を試す最小サンプルです。既存のAstroプロジェクトに自動でファイルを書き込むものではありません。

## 準備

Node.js 22.23.4以上とnpmを用意して、このフォルダで実行します。

```sh
npm ci
```

Tatamiの「ビルダー設定…」で以下を指定します。

- ビルダー：外部コマンド
- プロジェクト：この `examples/astro` フォルダ
- 実行ファイル：`node`（GUIのPATHにない場合は絶対パス）
- 引数：1行目 `--run`、2行目 `build`

「Astroの実行設定を入力」で実行ファイルと引数の初期値を設定できます。プロジェクトは別途選択してください。Tatamiでメモと出力先を選び「サイトをビルド」を押します。

## 接続方法

Tatamiは `publish: true` のMarkdownだけを一時フォルダへ書き出します。`:::private` ブロックを除去し、Front Matterは独自フィールドも含めて維持します。ファイル名は `{slug}.md` です。

- `TATAMI_CONTENT_DIR`：公開用Markdownの一時入力フォルダ
- `TATAMI_OUTPUT_DIR`：ビルダーが書き出す一時出力フォルダ

`src/content.config.ts` は公開用Markdownを読み込み、`src/pages/[id].astro` が各記事を生成します。`astro.config.mjs` が一時出力先を指定します。HTMLをrehype-sanitizeでサニタイズしています。Astro 7の公式Markdownプロセッサを使用します。

終了コード0かつHTMLが生成された場合だけ、TatamiがHTML・CSS・JS・画像等をユーザーの出力先に反映します。元のMarkdownは変更しません。処理終了後、一時フォルダは削除されます。

生成物にはルート基準のCSS参照があるため、確認にはTatami内のHTTPプレビューかHTTPサーバーを使用してください。`file://` で直接開いたときのCSS適用は保証しません。

## 既存のAstroプロジェクトへ組み込む場合

このサンプルのコンテンツローダーとoutDir設定を参考に、上記2つの環境変数を使うよう既存プロジェクトを設定してください。Tatamiは既存のAstroプロジェクトを自動変換しません。Astroのバージョンに合わせて設定APIを調整してください。

参考：
- https://docs.astro.build/en/guides/content-collections/
- https://docs.astro.build/en/reference/configuration-reference/

依存バージョンはpackage.jsonとpackage-lock.jsonに固定しています。ビルド検証はNode.js 26.13.0、Astro 7.4.13で行っています。
