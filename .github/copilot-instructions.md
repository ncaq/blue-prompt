# 出力設定

## 言語

AIは人間に話すときは日本語を使ってください。

しかし既存のコードのコメントなどが日本語ではない場合は、
コメント等は既存の言語に合わせてください。

## 記号

ASCIIに対応する全角形(Fullwidth Forms)は使用禁止。

具体的には以下のような文字:

- 全角括弧 `（）` → 半角 `()`
- 全角コロン `：` → 半角 `:`
- 全角カンマ `，` → 半角 `,`
- 全角数字 `０-９` → 半角 `0-9`

# 重要コマンド

## フォーマット

nix fmtでフォーマットとリントを実行できます。

```console
nix fmt
```

[nix-tasuke](https://github.com/ncaq/konoka/tree/master/plugins/nix-tasuke)プラグインにより、
Claudeの応答完了時にStopフックで`nix fmt`が自動実行されます。
ファイルの差分が出ることがあります。

## 統合チェック

nix-fast-buildコマンドで統合チェックを実行できます。

```console
nix-fast-build --option eval-cache false --no-link --skip-cached --no-nom
```

# リポジトリ構成

様々なコーディングエージェント向けの`AGENTS.md`と、
Claude Code向けの`CLAUDE.md`は、
以下のように`.github/copilot-instructions.md`のシンボリックリンクになっています。

```console
AGENTS.md -> .github/copilot-instructions.md
CLAUDE.md -> .github/copilot-instructions.md
```

これにより各種LLM向けのドキュメントを一元管理しています。

# F#

## ビルドとテスト

devShell内(direnvをロードした通常の環境)で、
通常のdotnetコマンドが使えます。

slnファイルは使っていないため、
プロジェクトのディレクトリを指定して実行します。

```console
dotnet build src/BluePrompt
dotnet test test/BluePrompt.Test
```

`dotnet test`は外部サイト依存テスト(`Category=Network`)も含めて実行します。
Nix経由の統合検証(外部サイト依存テストを除く)はnix-fast-buildのchecksに含まれています。

## リンター

F#のリンターとして[fsharp-analyzers](https://github.com/ionide/FSharp.Analyzers.SDK)を使っています。
ルール集はG-Research.FSharp.AnalyzersとIonide.Analyzersで、
Directory.Build.propsで全F#プロジェクトへ導入しています。

FSACがエディタで表示する診断(FSAC0001未使用open・FSAC0002冗長な修飾子・FSAC0003未使用宣言)も、
`src/BluePrompt.Analyzers`の自作アナライザーで同じ検出をリントとして再現しています。
FSACにはバッチ実行の手段が無いため、
FSACが内部で使うFSharp.Compiler.ServiceのEditorServices APIを直接呼んでいます。
FSharp.Analyzers.SDKのバージョンはflake.nixのfsharp-analyzersと一致させる必要があります。

リンターはnix-fast-buildの統合チェックの一部として自動実行されます。
警告もエラー扱いで、
違反があるとチェックが失敗します。

対象のプロジェクトはリポジトリ直下の`lint.proj`がglobで集約しているため、
プロジェクトを追加しても一覧の更新は不要です。

devShellで単体実行したい時は以下を使います。

```console
dotnet msbuild lint.proj /t:AnalyzeFSharpProject -m
```

特定のプロジェクトだけ解析したい時は、
プロジェクトのディレクトリを指定します。

```console
dotnet msbuild src/BluePrompt /t:AnalyzeFSharpProject
```

## NuGet依存の更新

fsprojのPackageReferenceを変更したら以下を一回実行するだけで、
deps.jsonの再生成・整形・コミットまで自動で完了します。

```console
nix run .#update-deps
```

# wikiruナレッジの生成

`plugins/jp-wikiru-bluearchive/`配下のスキルのナレッジは、
wikiruの記事からの自動生成ファイルです。
手で編集せず、以下のコマンドで再生成してください。
ナレッジを生成する`wikiru-knowledge`と`wikiru-student-skill`は、
書き出した直後に`nix fmt`まで実行するので、
生成コマンドだけで内容が確定します。

一覧ページを整形するスキルの`reference.md`は以下で生成します。

```console
dotnet run --project src/BluePrompt -- wikiru-knowledge '<ページ名>' <出力ファイル>
```

生徒個別のスキルはナレッジを埋め込んだ`SKILL.md`全体を以下で生成します。
スキル名は出力先のディレクトリ名から導出されます。

```console
dotnet run --project src/BluePrompt -- wikiru-student-skill '<生徒のページ名>' <SKILL.mdの出力パス>
```

抽出設定を調整する時は、
以下でpandoc変換前の中間HTMLを確認できます。
こちらは`nix fmt`を実行しません。

```console
dotnet run --project src/BluePrompt -- wikiru-html '<ページ名>' <出力ファイル>
```

生徒個別ページは折りたたみを残すなど抽出設定が異なるため、
生徒個別ページの設定での中間HTMLは以下で確認します。
こちらも`nix fmt`を実行しません。

```console
dotnet run --project src/BluePrompt -- wikiru-student-html '<生徒のページ名>' <出力ファイル>
```

# テスト方針

このリポジトリのプログラムは外部に配布したりする性質のものではないので、
動いていることが確認できれば問題なく、
テストカバレッジはあまり気にしません。

バグったときとか、
機能の確認でインラインで動作確認するより良いからテストを書いているだけです。
