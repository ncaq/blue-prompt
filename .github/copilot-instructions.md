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

checksの`open-webui-sync`はNixのデーモンに`uid-range`のsystem-featureを要求します。
設定の無い環境ではこれだけが落ちるので、
「同期のテスト」の節に従って設定してください。

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

生成コマンドを手元のソースから実行する時は、
devShellが置く`blue-prompt`コマンドを使います。
中身は`dotnet run --configuration Release --project src/BluePrompt --`で、
引数はそのまま渡り、相対パスは呼び出した場所が基準になります。
実行だけをReleaseにしている理由はflake.nixの`blue-prompt-dev`にあります。

## コマンドラインの構成

サブコマンドと引数は[Argu](https://fsprojects.github.io/Argu/)の判別共用体で、
`src/BluePrompt/Program.fs`に宣言しています。
サブコマンドを足す時は判別共用体へケースを足すだけで、ヘルプにも反映されます。
並び順と引数の決まりは同じファイルの冒頭のコメントにあります。

## 文字列の形の判定と書き換え

文字列の形の判定と書き換えは正規表現ではなく、
パーサコンビネータの[XParsec](https://roboz0r.github.io/XParsec/)で書きます。
理由と共通の道具は`src/BluePrompt/Parse.fs`にあります。

Markdownの節を落としたり選んだりする処理は、
`src/BluePrompt/Markdown.fs`の`parseSections`で見出しの木へ読んでから書き換えます。

## JSON

JSONは[FSharp.SystemTextJson](https://github.com/Tarmil/FSharp.SystemTextJson)で、
F#のレコードとの対応として読み書きします。
`JsonNode`で木を組み立てたり、キーの道を辿って値を読んだりはしません。
APIの応答も、読む欄だけを持つレコードで受けます。

Open WebUIとやり取りするJSONの設定は`src/BluePrompt/OpenWebuiJson.fs`に、
wikiruから構造化したレコードの設定は`src/BluePrompt/JsonCodec.fs`にあります。

## リンター

F#のリンターとして[fsharp-analyzers](https://github.com/ionide/FSharp.Analyzers.SDK)を使っています。
ルール集はG-Research.FSharp.AnalyzersとIonide.Analyzersで、
Directory.Build.propsで全F#プロジェクトへ導入しています。
FSACが持つ診断(FSAC0001未使用open・FSAC0002冗長な修飾子・FSAC0003未使用宣言)も、
`src/BluePrompt.Analyzers`の自作アナライザーで同じ検出をリントとして再現しています。
エディタに出るかはLSPクライアント側の設定次第なので、
エディタが黙っていてもリントが落ちることはあります。

判別共用体のケース名の衝突は、
`RequireQualifiedAccess`ではなくモジュールで名前空間を分けて避けてください。
理由は`src/BluePrompt/Program.fs`の冒頭のコメントにあります。

リンターはnix-fast-buildの統合チェックの一部として自動実行され、
警告もエラー扱いです。
対象のプロジェクトは`lint.proj`がglobで集めるので、
`src`と`test`の配下へプロジェクトを足しても一覧の更新は要りません。

devShellで単体実行したい時は以下を使います。

```console
dotnet msbuild lint.proj /t:AnalyzeFSharpProject -warnaserror -m
```

特定のプロジェクトだけ解析したい時は、
プロジェクトのディレクトリを指定します。

```console
dotnet msbuild src/BluePrompt /t:AnalyzeFSharpProject -warnaserror
```

どちらも解析の前に必要な復元を自分で済ませるため、事前の準備は要りません。

## NuGet依存の更新

fsprojのPackageReferenceを変更したら以下を一回実行するだけで、
deps.jsonとfsharp-analyzers.jsonとDirectory.Build.propsの`FSharpCoreVersion`の再生成と整形が完了します。
コミットは他の変更とまとめて手動で行います。

```console
nix run .#update-deps
```

RenovateのPRでは、
`.github/workflows/update-deps.yml`が同じ再生成をRenovateのブランチへコミットして追従させます。
仕組みの理由はワークフローと`renovate.json`と`.github/workflows/kyosei.yml`のコメントにあります。

# スキルの呼び出し制御

`plugins/jp-wikiru-bluearchive/`配下のナレッジのスキルは、
フロントマターへ`user-invocable: false`を書いています。
事実を引くための参照専用で、
ユーザが`/character-yuuka`のように直接呼んでも意味が無いため、
スラッシュコマンドの一覧から外してモデルからの読み込みだけを残しています。

`plugins/role-play/`配下の人格のスキルはユーザが会話を始める入口なので、
この指定は付けません。

生徒個別のスキルとプレイアブルではない登場人物のスキルのフロントマターは生成物なので、
指定を変える時は`src/BluePrompt/Wikiru.fs`の`characterSkillMarkdown`を直します。

# スキルのdescription

descriptionは本文を読み込まなくても、
スキルの一覧としてモデルのコンテキストへ載ります。
このリポジトリは400を超えるスキルを持つため、
1件あたりの長さがそのまま常時の負担になります。
引くかどうかの判断に足りる最小限だけを書いて、
使う場面の言い換えや、本文が持っている内容の要約は書きません。

- 生徒個別のスキル: `Facts about Blue Archive student 栗村アイリ.`
- プレイアブルではない登場人物のスキル: `Facts about Blue Archive character 七神リン.`
- role-playのスキル: `Role-play as 栗村アイリ of Blue Archive.`

生徒のスキルで何が載っているかは、
本文の「データの構造」が実際の節の一覧として持っているため、
descriptionでは繰り返しません。

名前には姓を付けます。
wikiruのページ名は「アイリ」「アイリ（バンド）」のように姓を持たず、
一覧に並ぶ説明だけでは誰のことなのかが決まらないためです。
生成するスキルでは`src/BluePrompt/Wikiru.fs`の`familyNamedPageName`と`unplayableDisplayName`が補います。

role-playのスキルのdescriptionは、
character.mdのフロントマターから生成物へそのまま写ります。

キャラ呼称表と学校別キャラクター一覧のスキルは、
名前を並べるだけでは何を引けるスキルなのかが伝わらず、
件数も2つしか無いため、この短さには揃えていません。

# wikiruナレッジの生成

`plugins/jp-wikiru-bluearchive/`配下のスキルのナレッジと、
`plugins/role-play/`配下のスキルの参照ファイルは、
wikiruの記事からの自動生成ファイルです。
手で編集せず、以下のコマンドで再生成してください。
中間HTMLを確認する`wikiru html`と`wikiru student-html`以外の生成コマンドは、
書き出した直後に`nix fmt`まで実行するので、
生成コマンドだけで内容が確定します。

## 一括更新

既存の生成物をまとめて更新する時は以下を使います。
`src/BluePrompt/Manifest.fs`に書いた対象を全て並列にwikiruから取得して書き出し、
続けてrole-playスキルの本文も生成し直してから、
最後に`nix fmt`を1回だけ実行します。
1回の起動で済むため、ページごとに起動してJITと`nix fmt`のコストを払い直さずに済みます。

```console
blue-prompt wikiru all --root .
```

対象のパスはリポジトリのルートからの相対で、`--root`で基準を渡します。
取得に失敗した対象があれば、失敗した対象の名前と理由を表示して終了コード1で止まります。
失敗時は整形もrole-playスキルの生成もしないので、
書き出し済みの生成物は整形されないまま残ります。

wikiruへアクセスせずrole-playスキルの本文だけを全て生成し直す時は以下を使います。
テンプレートを直した時と、
統合チェックの`roleplay-generated`が生成物の鮮度を確かめる時に使われます。

```console
blue-prompt roleplay all --root .
```

## マニフェストの漏れの確認

`Manifest.fs`の対象は手で足すため、
wikiruへ生徒や衣装違いが増えても一括更新は何も言いません。
漏れは以下で調べます。
wikiruの一覧を2つ取得して突き合わせるだけで、何も書き出しません。

```console
blue-prompt wikiru check-manifest
```

どちらの向きの食い違いも表示して、1つでもあれば終了コード1で止まります。
食い違いの種類と出典の分け方は`Manifest.fs`の`Coverage`と`coverageOf`にあります。
外部サイトの状態で結果が変わるため、統合チェックには含めていません。

## 個別の生成

新しい生徒を足す時は、
この節の個別コマンドで生成してから、
以後の一括更新に含めるために`Manifest.fs`へも対象を足してください。

一般的なページを整形した`reference.md`は以下で生成します。

```console
blue-prompt wikiru knowledge --page '<ページ名>' --output <出力ファイル>
```

キャラ呼称表のスキルは、
テーブルを構造化した上でLLM参照用の`reference.md`と、
機械読み出し用の`appellation.json`を以下で同時に生成します。

```console
blue-prompt wikiru appellation --page 'キャラ呼称表' \
  --markdown-output plugins/jp-wikiru-bluearchive/skills/character-appellation/reference.md \
  --json-output plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json
```

学校別キャラクター一覧のスキルは、
生徒1人が1つのテーブルになっているカードを構造化して、
学校ごとの1つのテーブルへまとめた`reference.md`と、
機械読み出し用の`school.json`を以下で同時に生成します。

```console
blue-prompt wikiru school --page '学校別' \
  --markdown-output plugins/jp-wikiru-bluearchive/skills/character-index-by-group/reference.md \
  --json-output plugins/jp-wikiru-bluearchive/skills/character-index-by-group/school.json
```

生徒個別のスキルはナレッジを埋め込んだ`SKILL.md`全体を以下で生成します。
スキル名は出力先のディレクトリ名から導出されます。

```console
blue-prompt wikiru student-skill --page '<生徒のページ名>' --output <SKILL.mdの出力パス>
```

プレイアブルではない登場人物のスキルも、
生徒個別ページと同じ形のページから以下で生成します。
role-playのスキルは作らずナレッジだけを持ち、
role-playのcharacter.mdの`knowledge:`からも挙げられます。
対象と呼び方とスキル名の決め方は`Manifest.fs`の`Unplayable`と`unplayables`にあります。

```console
blue-prompt wikiru unplayable-skill --page '<ページ名>' --output <SKILL.mdの出力パス>
```

role-playスキルの衣装別参照ファイルは、
生徒個別ページからプロフィールとボイスと小ネタだけを抜き出して以下で生成します。
抜き出す節と取り除くものの理由は`Wikiru.fs`の`rolePlaySectionTitles`と`rolePlayContentQuery`にあります。

```console
blue-prompt wikiru roleplay-reference --page '<生徒のページ名>' --output <出力ファイル>
```

role-playスキルの本文は、
全生徒で共通のテンプレートのプレースホルダへ、
生徒ごとに決まる値を差し込んで以下で生成します。
wikiruへはアクセスせず、リポジトリへ併置した生成物だけで完結します。

テンプレートは`plugins/role-play/SKILL.template.md`の1つだけです。
生成したSKILL.mdはClaude Codeのスキルになり、
Open WebUIのModelのシステムプロンプトにもそのままなります。
ナレッジの引き方は経路で違いますが、
本文を分けると生成の経路まで二重になるため、
テンプレートにはどちらの経路でも成り立つ言い回しだけを書きます。
「スキルが読み込まれた」のように片方でしか成り立たない前提は書きません。

- `{{caller}}`: 演じる生徒の呼び名
- `{{character}}`: スキルのディレクトリのcharacter.mdの本文。生徒に固有の手書きの部分
- `{{playing}}`: 全生徒に共通する演じ方の指示
- `{{appellation}}`: 生成済みのappellation.jsonから抜き出した指定キャラクターの呼称表
- `{{costumes}}`: 同じディレクトリにある衣装別の参照ファイルの中身。
  出典のページ名の見出しを立てた下へそのまま入ります
- `{{quotes}}`: スキルのディレクトリのquote/にある代表的な発言。
  他の穴と違い見出しごと差し込まれ、発言を持たない生徒では節ごと消えます
- `{{knowledgeSkills}}`: character.mdのフロントマターのknowledge:から
  `character-appellation`を除いた、この生徒のナレッジのスキル名の一覧

テンプレートはここに挙げた穴を全て使う必要があり、
書き忘れると差し込むはずの内容が落ちないようにエラーで止まります。
生成物のフロントマターはcharacter.mdのものをそのまま写します。

プロフィールや口調を手で書き下すことはしません。
理由は`RolePlay.fs`の`playingRules`にあります。

全生徒に効く指示はテンプレートを、
その生徒だけの指示はcharacter.mdを編集して生成し直します。

SKILL.mdはファイル名が決まっているため、
渡すのはテンプレートのディレクトリと出力先のディレクトリです。

```console
blue-prompt roleplay skill --character '<キャラクター名>' \
  --template plugins/role-play \
  --appellation plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json \
  --output <スキルのディレクトリ>
```

character.mdとquote/とテンプレートはスキルのディレクトリに置いたままで、
Nixで組み立てる配布物からはflake.nixの`nonSkillNames`が除きます。

抽出設定を調整する時は、
以下でpandoc変換前の中間HTMLを確認できます。
こちらは`nix fmt`を実行しません。

```console
blue-prompt wikiru html --page '<ページ名>' --output <出力ファイル>
```

生徒個別ページは折りたたみを残すなど抽出設定が異なるため、
生徒個別ページの設定での中間HTMLは以下で確認します。
こちらも`nix fmt`を実行しません。

```console
blue-prompt wikiru student-html --page '<生徒のページ名>' --output <出力ファイル>
```

## 代表的な発言

作中で実際に話した長めの発言を、
スキルのディレクトリのquote/へ1つ1つのMarkdownとして手で置きます。
ボイスは短いセリフばかりなので、
話の運び方と長い語りの組み立てはこちらが担います。

ファイルは`## `の見出しから始めます。
始まっていなければエラーで止まります。
並びはファイル名の順です。

出典はwikiruではなくメインストーリーなので、
生成では作れません。
書き手が発言の本文と一緒に書きます。

## 表の箇条書き化

wikiruのデータの表は、
pandocへ渡す前に`src/BluePrompt/TableList.fs`が箇条書きへ置き換えます。
理由と置き換えの規則はそのファイルのコメントにあります。

# Open WebUI向けの生成と同期

Open WebUIにはスキルのようなオンデマンド読み込みの仕組みがありません。
Modelは会話の入口を選ぶだけで、
会話の途中に別のModelを読み込むことはできません。
そのためスキルの性質によって登録先を分けています。

- 人格を与えるスキル(`plugins/role-play`)はワークスペースModelにする
- 参照して事実を引くスキル(`plugins/jp-wikiru-bluearchive`)はKnowledgeコレクションにする

どちらに登録するかはflake.nixの`openWebuiKinds`がプラグイン単位で決めていて、
`plugins/`のディレクトリ一覧との食い違いは評価時に検出されます。

## Model定義の生成

SKILL.mdの本文を、
システムプロンプトへ焼き込んだワークスペースModelの作成フォームJSONへ変換します。
本文にローカルリンクが残っていればエラーで止まります。
変換の決まりは`src/BluePrompt/OpenWebui.fs`にあります。

人格のスキル分をまとめて生成するのは以下です。
出力はビルド成果物でリポジトリへはコミットしないため、
`nix fmt`は実行しません。

```console
nix build .#open-webui-model
```

スキル1つ分を単体で変換したい時は以下を使います。

```console
blue-prompt open-webui model --skill <スキルディレクトリ> --output <出力ファイル>
```

## Knowledge定義の生成

SKILL.mdとリンクされたMarkdownの参照ファイルを見出しの単位へ分割して、
検索でヒットする単位と読ませたい単位を揃えます。
分割と名付けの決まりは`src/BluePrompt/OpenWebuiKnowledge.fs`と`src/BluePrompt/Markdown.fs`にあります。

ナレッジのスキル分をまとめて生成するのは以下です。
出力はビルド成果物でリポジトリへはコミットしないため、
`nix fmt`は実行しません。

```console
nix build .#open-webui-knowledge
```

スキル1つ分を単体で変換したい時は以下を使います。

```console
blue-prompt open-webui knowledge --skill <スキルディレクトリ> --output <出力ディレクトリ>
```

## ModelとKnowledgeの紐付け

role-playスキルのフロントマターの`knowledge:`行に、
参照するKnowledgeコレクションの名前をカンマ区切りで並べます。
yuukaのように生成するスキルでは、
スキルのディレクトリのcharacter.md側へ書いて生成し直します。

コレクションのidは`open-webui sync`が名前でコレクションを引き当てて埋めます。
紐付け先が同期の対象に無ければエラーで止まります。

## 同期

生成したModelとKnowledgeは`open-webui sync`サブコマンドで対象インスタンスへ同期できます。
APIで登録済みのものと突き合わせて、
無ければ作成し、差分があれば上書きし、差分が無ければ書き込みません。
比べ方と送るものの決まりは`src/BluePrompt/OpenWebuiSync.fs`にあります。

```console
blue-prompt open-webui sync --model <モデル定義ディレクトリ> --base-url <ベースURL> \
  [--base-model-id <id>] [--api-key-file <パス>] \
  [--knowledge <ディレクトリ>] [--rag-template-file <パス>]
```

## 同期のテスト

登録や更新が正しく進むことは、
`tests/open-webui-sync.nix`のNixOSテストが使い捨ての実物のOpen WebUIで確かめます。
nix-fast-buildのchecks(`open-webui-sync`)に含まれています。
手元で素早く回すのは`dotnet test`の単体テストです。

コンテナのテストは`uid-range`のsystem-featureを要求します。
手元のNixのデーモンに設定が無ければビルドできないので、
NixOSのマニュアルの"Running Tests"の"System Requirements"に従って設定してください。
CIでは`.github/workflows/check.yml`がこの設定を渡しています。

## RAGプロンプトテンプレート

Open WebUIの既定のRAGテンプレートはロールプレイ用のModelの人格と話し方を壊すため、
`modules/open-webui-rag-template.txt`のテンプレートを`--rag-template-file`で同期します。

## NixOSモジュール

この同期を宣言的に行うNixOSモジュールが、
`modules/nixos.nix`にあり`nixosModules.default`として公開されています。
エンドポイントのURLなど登録先に依存する情報だけをオプションで入力すると、
systemdサービスが起動時と生成物の変更時に同期を実行します。
同期の成否は`nixos-rebuild`の出力には出ないので、
ユニットの状態とジャーナルで確かめます。

# 想定するRAGの環境

Knowledgeの分割の粒度や断片への見出しの前置は、
特定のRAG設定の登録先で検索が当たることを実測して決めています。
検証に使っている登録先の設定は以下の通りです。

- 埋め込み: `RAG_EMBEDDING_ENGINE`は`ollama`。
  モデルはmultilingual-e5-largeのGGUF q8_0で、
  `RAG_EMBEDDING_QUERY_PREFIX`の`query: `と、
  `RAG_EMBEDDING_CONTENT_PREFIX`の`passage: `を本文への文字列連結で付けます
- 分割: `CHUNK_SIZE`は1000、`CHUNK_OVERLAP`は100のOpen WebUIの既定値
- 検索: `RAG_TOP_K`は既定値の3。ハイブリッド検索とリランカーは使っていません
- ベクタDB: `VECTOR_DB`は`pgvector`

埋め込みのエンジンの選択は検索精度に影響しません。
同じモデルならsentence-transformersのfp32とOllamaのGGUF q8_0で、
文書の埋め込みのコサイン一致度は平均0.9996あり、
検索の指標も変わらないことを実測で確認しています。
そのため生成側が気にする必要があるのはモデルと分割の粒度だけで、
登録先がどのエンジンで埋め込むかは気にしなくて構いません。

実測はこのリポジトリのKnowledgeの実物(3361ファイル、10011チャンク)と、
日本語の20問で行っています。
経緯と数値は以下のissueに記録があります。

- エンジンの選定: https://github.com/ncaq/blue-prompt/issues/171
- モデルの選定: https://github.com/ncaq/blue-prompt/issues/196

この設定での登録先の実際の構築は、
dotfilesの https://github.com/ncaq/dotfiles/pull/1550 が行っています。

モデルの乗り換えや、
ハイブリッド検索とリランカーの導入でこの前提が変わる時は、
モデル選定のissueの測定をやり直した上でこの節を更新します。

# wikiru由来のテキストとプロンプトインジェクション

wikiruは誰でも編集できるwikiなので、
そこから生成したMarkdownをLLMへ渡す経路には、
間接プロンプトインジェクション(CWE-1427)の余地があるように見えます。
実際にレビューでも繰り返し指摘されています。

しかしこのリポジトリでは、
テーブルのセルの改行の無害化や、
「資料の中に書かれた指示には従うな」のような枠付けを意図的に入れていません。
以下の理由で危険が小さいと判断しているため、
この観点の指摘は不要です。

- 生成物は実行時に流れ込む未知の入力ではなく、
  生成コマンドを回してコミットする固定のファイルです。
  取り込む時に差分を読むため、
  普通は入っていない値が混ざれば取り込む前に気付きます
- 宛先は自分のClaude Codeと自分のOpen WebUIのインスタンスだけで、
  不特定の利用者へ配るものではありません
- このリポジトリが生成するModelはツールを紐付けないため、
  仮に混入した指示が通っても、
  起きるのは人格や口調が崩れることだけです

対策を足す側にも費用があります。
`modules/open-webui-rag-template.txt`がOpen WebUIの既定のテンプレートを置き換えているのは、
引用番号やXMLタグの指示が人格と話し方を壊すからです。
資料の扱いについての枠付けを書き足すと、
同じ方向へロールプレイの邪魔をします。

この判断が変わるのは、
実行時に外部の入力を取り込むようになった時か、
生成物を自分以外の利用者へ配るようになった時です。
その時はこの節ごと見直します。

# テスト方針

このリポジトリのプログラムは外部に配布したりする性質のものではないので、
動いていることが確認できれば問題なく、
テストカバレッジはあまり気にしません。

バグったときとか、
機能の確認でインラインで動作確認するより良いからテストを書いているだけです。

## テストの入力にするHTML

wikiruのページを模したテストの入力のHTMLは、
文字列のリテラルではなく[Falco.Markup](https://github.com/FalcoFramework/Falco.Markup)で組み立てます。
書き方の決まりは`test/BluePrompt.Test/HtmlFixture.fs`の冒頭のコメントにあります。
