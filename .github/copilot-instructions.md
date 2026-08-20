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

対象のプロジェクトはリポジトリ直下の`lint.proj`が、
`src`と`test`配下の再帰globで集約しているため、
その配下にプロジェクトを追加する限り一覧の更新は不要です。

devShellで単体実行したい時は以下を使います。

```console
dotnet msbuild lint.proj /t:AnalyzeFSharpProject -warnaserror -m
```

特定のプロジェクトだけ解析したい時は、
プロジェクトのディレクトリを指定します。

```console
dotnet msbuild src/BluePrompt /t:AnalyzeFSharpProject -warnaserror
```

## NuGet依存の更新

fsprojのPackageReferenceを変更したら以下を一回実行するだけで、
deps.jsonの再生成・整形・コミットまで自動で完了します。

```console
nix run .#update-deps
```

# wikiruナレッジの生成

`plugins/jp-wikiru-bluearchive/`配下のスキルのナレッジと、
`plugins/role-play/`配下のスキルの参照ファイルは、
wikiruの記事からの自動生成ファイルです。
手で編集せず、以下のコマンドで再生成してください。
中間HTMLを確認する`wikiru-html`と`wikiru-student-html`以外の生成コマンドは、
書き出した直後に`nix fmt`まで実行するので、
生成コマンドだけで内容が確定します。

一般的なページを整形した`reference.md`は以下で生成します。

```console
dotnet run --project src/BluePrompt -- wikiru-knowledge '<ページ名>' <出力ファイル>
```

キャラ呼称表のスキルは、
テーブルを構造化した上でLLM参照用の`reference.md`と、
機械読み出し用の`appellation.json`を以下で同時に生成します。

```console
dotnet run --project src/BluePrompt -- wikiru-appellation 'キャラ呼称表' plugins/jp-wikiru-bluearchive/skills/character-appellation/reference.md plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json
```

生徒個別のスキルはナレッジを埋め込んだ`SKILL.md`全体を以下で生成します。
スキル名は出力先のディレクトリ名から導出されます。

```console
dotnet run --project src/BluePrompt -- wikiru-student-skill '<生徒のページ名>' <SKILL.mdの出力パス>
```

role-playスキルの衣装別参照ファイルは、
生徒個別ページからプロフィールとボイスだけを抜き出して以下で生成します。

```console
dotnet run --project src/BluePrompt -- wikiru-roleplay-reference '<生徒のページ名>' <出力ファイル>
```

role-playスキルのSKILL.mdは、
同じディレクトリの手書きテンプレートSKILL.template.mdのプレースホルダへ、
生成済みのappellation.jsonから抜き出した指定キャラクターの呼称表を流し込んで、
以下で全体を生成します。
wikiruへはアクセスしません。
人格や口調の指示を変える時はSKILL.mdではなくテンプレートを編集して生成し直します。

```console
dotnet run --project src/BluePrompt -- roleplay-skill '<キャラクター名>' plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json <SKILL.mdの出力パス>
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

# Open WebUI向けモデル定義の生成

Open WebUIにはスキルのようなオンデマンド読み込みの仕組みが無いため、
SKILL.mdと本文から明示的にリンクされた参照ファイルをインライン化して、
システムプロンプトへ焼き込んだワークスペースModelの作成フォームJSONへ変換します。
生成物は`POST /api/v1/models/create`へそのまま渡して登録できる形式です。

全スキル分をまとめて生成するのは以下です。
出力はビルド成果物でリポジトリへはコミットしないため、
`nix fmt`は実行しません。

```console
nix build .#open-webui-model
```

スキル1つ分を単体で変換したい時は以下を使います。

```console
dotnet run --project src/BluePrompt -- open-webui-model <スキルディレクトリ> <出力ファイル>
```

# テスト方針

このリポジトリのプログラムは外部に配布したりする性質のものではないので、
動いていることが確認できれば問題なく、
テストカバレッジはあまり気にしません。

バグったときとか、
機能の確認でインラインで動作確認するより良いからテストを書いているだけです。
