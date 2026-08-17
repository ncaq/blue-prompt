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

`dotnet test`はブラウザ依存テスト(`Category=Browser`)も含めて実行します。
Nix経由の統合検証(ブラウザ依存テストを除く)はnix-fast-buildのchecksに含まれています。

## NuGet依存の更新

fsprojのPackageReferenceを変更したら以下を一回実行するだけで、
deps.jsonの再生成・整形・コミットまで自動で完了します。

```console
nix run .#update-deps
```

`Microsoft.Playwright`のバージョンは、
nixpkgsの`playwright-driver`とmajor.minorを揃える必要があります。
ズレるとflake評価がassertで失敗します。

そのためRenovateには`renovate.json`でパッチ更新のみ許可する制限を掛けています。
nixpkgsの`playwright-driver`が上がった時は、
`renovate.json`の`allowedVersions`も手で追従させてください。

# テスト方針

このリポジトリのプログラムは外部に配布したりする性質のものではないので、
動いていることが確認できれば問題なく、
テストカバレッジはあまり気にしません。

バグったときとか、
機能の確認でインラインで動作確認するより良いからテストを書いているだけです。
