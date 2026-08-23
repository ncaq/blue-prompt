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

## コマンドラインの構成

サブコマンドと引数は[Argu](https://fsprojects.github.io/Argu/)の判別共用体で宣言しています。
ヘルプは`Usage`から自動生成されるため、手書きの使い方の文字列はありません。
サブコマンドを足す時は`src/BluePrompt/Program.fs`の判別共用体へケースを足すだけで、
ヘルプにも反映されます。

サブコマンドは外部システムとの接点で2階層に分かれています。

- `wikiru`: wikiruへ取りに行くもの
- `roleplay`: 外部と話さずリポジトリ内の生成物だけで完結するもの
- `open-webui`: Open WebUI向けに生成するものと、インスタンスへ送るもの

第1階層はこの取り込みから送り出しへの流れの順に並べ、
各グループの中は辞書順にして、
生成物を作らない確認用の`html`と`student-html`だけを末尾へ置いています。
順序の定義は判別共用体のケースの並びだけにあり、
ヘルプの並びもそこから決まるので、二重管理になりません。

引数無しの起動とトップレベルのヘルプのフラグ(`--help`/`-h`/`help`)は、
グループの一覧で止めずに末端のコマンドの引数まで全て展開して表示します。
この規模なら階層を辿らせるより一度に見せるほうが早いためです。
`blue-prompt wikiru --help`のように階層を指定した時はArgu既定のその階層だけの表示になります。

引数は位置引数を使わず全て名前付きオプションです。
必須のものは`ExactlyOnce`で縛っているため、
渡し忘れも重複もArguが弾きます。

## リンター

F#のリンターとして[fsharp-analyzers](https://github.com/ionide/FSharp.Analyzers.SDK)を使っています。
ルール集はG-Research.FSharp.AnalyzersとIonide.Analyzersで、
Directory.Build.propsで全F#プロジェクトへ導入しています。

FSACが持つ診断(FSAC0001未使用open・FSAC0002冗長な修飾子・FSAC0003未使用宣言)も、
`src/BluePrompt.Analyzers`の自作アナライザーで同じ検出をリントとして再現しています。
FSACにはバッチ実行の手段が無いため、
FSACが内部で使うFSharp.Compiler.ServiceのEditorServices APIを直接呼んでいます。
FSharp.Analyzers.SDKのバージョンはflake.nixのfsharp-analyzersと一致させる必要があります。
これらをエディタにも出すかはLSPクライアント側の設定次第なので、
エディタが黙っていてもリントが落ちることはあります。

FSAC0002のもとになるFCSのSimplifyNamesは`RequireQualifiedAccess`を見ておらず、
省略するとコンパイルが通らない修飾まで冗長として報告します。
判別共用体のケース名の衝突は、この属性ではなくモジュールで名前空間を分けて避けてください。

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
deps.jsonの再生成と整形が完了します。
コミットは他の変更とまとめて手動で行います。

```console
nix run .#update-deps
```

RenovateはfsprojとDirectory.Build.propsのPackageReferenceしか書き換えないため、
そのままではdeps.jsonが古いまま残りnix-fast-buildが落ちます。
Mendのホスト版RenovateはpostUpgradeTasksを実行できないので、
代わりに`.github/workflows/update-deps.yml`が、
Renovateのブランチへdeps.jsonの再生成をコミットして追従させます。

コミットはGitのpushではなくcontents APIへの書き込みで作ります。
GitHubがサーバ側で署名するためVerifiedになり、
同じ経路で作られるRenovate自身のコミットと揃うからです。

トークンはGITHUB_TOKENではなく専用のGitHub Appのものを使います。
GITHUB_TOKENが作ったコミットはワークフローを起動しない仕様のため、
必須チェックのnix-fast-buildが最新のコミットで走らないまま止まってしまうからです。
Client IDはActionsのvariableの`UPDATE_DEPS_APP_CLIENT_ID`に、
秘密鍵はsecretの`UPDATE_DEPS_APP_PRIVATE_KEY`にあります。

Renovateは自分以外の作者のコミットがあるブランチを、
人手で編集されたとみなして以後の更新を止めます。
このワークフローのコミットで止まらないように、
renovate.jsonの`gitIgnoredAuthors`が作者を無視の対象にしています。

# スキルの呼び出し制御

`plugins/jp-wikiru-bluearchive/`配下のナレッジのスキルは、
フロントマターへ`user-invocable: false`を書いています。
事実を引くための参照専用で、
ユーザが`/character-yuuka`のように直接呼んでも意味が無いため、
スラッシュコマンドの一覧から外してモデルからの読み込みだけを残しています。

`plugins/role-play/`配下の人格のスキルはユーザが会話を始める入口なので、
この指定は付けません。

生徒個別のスキルのフロントマターは生成物なので、
指定を変える時は`src/BluePrompt/Wikiru.fs`の`studentSkillMarkdown`を直します。

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
dotnet run --project src/BluePrompt -- wikiru all --root .
```

対象の一覧はJSONのような外部の設定ファイルではなく、
F#の値として`Manifest.fs`に書いています。
コンパイラが型を検査し、コメントも書け、パーサも要らないためです。
個別コマンドと同じ生成対象の型(`src/BluePrompt/Target.fs`)を使うので、
種別と引数の対応は1箇所にしかありません。
対象のパスはリポジトリのルートからの相対で、`--root`で基準を渡します。

並列度は`Manifest.fs`の`degreeOfParallelism`で固定しています。
wikiru側の負荷は1台のPCから送る量では誤差ですが、
無駄に並べても利益が無いため常識的な数に収めています。

取得に失敗した対象があっても他の対象は最後まで走り、
成功した分だけを整形してから、失敗した対象ごとの理由を並べて終了コード1で止まります。
role-playスキルは呼称表と衣装別の参照ファイルを読むため、
失敗時には生成し直しません。

wikiruへアクセスせずrole-playスキルの本文だけを全て生成し直す時は以下を使います。
テンプレートを直した時と、
統合チェックの`roleplay-generated`が生成物の鮮度を確かめる時に使われます。

```console
dotnet run --project src/BluePrompt -- roleplay all --root .
```

## 個別の生成

新しい生徒を足す時は、
この節の個別コマンドで生成してから、
以後の一括更新に含めるために`Manifest.fs`へも対象を足してください。

一般的なページを整形した`reference.md`は以下で生成します。

```console
dotnet run --project src/BluePrompt -- wikiru knowledge --page '<ページ名>' --output <出力ファイル>
```

キャラ呼称表のスキルは、
テーブルを構造化した上でLLM参照用の`reference.md`と、
機械読み出し用の`appellation.json`を以下で同時に生成します。

```console
dotnet run --project src/BluePrompt -- wikiru appellation --page 'キャラ呼称表' \
  --markdown-output plugins/jp-wikiru-bluearchive/skills/character-appellation/reference.md \
  --json-output plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json
```

学校別キャラクター一覧のスキルは、
生徒1人が1つのテーブルになっているカードを構造化して、
学校ごとの1つのテーブルへまとめた`reference.md`を以下で生成します。

```console
dotnet run --project src/BluePrompt -- wikiru school --page '学校別' \
  --output plugins/jp-wikiru-bluearchive/skills/character-index-by-group/reference.md
```

生徒個別のスキルはナレッジを埋め込んだ`SKILL.md`全体を以下で生成します。
スキル名は出力先のディレクトリ名から導出されます。

```console
dotnet run --project src/BluePrompt -- wikiru student-skill --page '<生徒のページ名>' --output <SKILL.mdの出力パス>
```

role-playスキルの衣装別参照ファイルは、
生徒個別ページからプロフィールとボイスだけを抜き出して以下で生成します。

```console
dotnet run --project src/BluePrompt -- wikiru roleplay-reference --page '<生徒のページ名>' --output <出力ファイル>
```

role-playスキルの本文は、
全生徒で共通のテンプレートのプレースホルダへ、
生徒ごとに決まる値を差し込んで以下で生成します。
wikiruへはアクセスせず、リポジトリへ併置した生成物だけで完結します。

テンプレートは届け先ごとに2つあります。

- `plugins/role-play/SKILL.template.md`: Claude Code向け。SKILL.mdになります
- `plugins/role-play/MODEL.template.md`: Open WebUIのModel向け。MODEL.mdになります

参照ファイルの届き方とナレッジの引き方が経路で違うため、
噛み合わない数文のために本文ごと分けています。
`open-webui model`はMODEL.mdがあればSKILL.mdより優先して使います。
2つのテンプレートはほとんど同じなので、片方を直したらもう片方も見てください。

- `{{caller}}`: 演じる生徒の呼び名
- `{{character}}`: スキルのディレクトリのcharacter.mdの本文。生徒に固有の手書きの部分
- `{{playing}}`: 全生徒に共通する演じ方の指示
- `{{appellation}}`: 生成済みのappellation.jsonから抜き出した指定キャラクターの呼称表
- `{{costumes}}`: 同じディレクトリにある衣装別の参照ファイルの一覧
- `{{knowledgeSkills}}`: character.mdのフロントマターのknowledge:から
  `character-appellation`を除いた、この生徒のナレッジのスキル名の一覧

テンプレートはこの6つを全て使う必要があり、
書き忘れると差し込むはずの内容が落ちないようにエラーで止まります。
生成物のフロントマターはcharacter.mdのものをそのまま写します。

プロフィールや口調をスキル本体へ書き下すことはしません。
プロフィールは衣装別の参照ファイルがどちらの経路でも届くため重複で、
口調の要約は目を引かれた特徴だけを強めてボイスの持つ幅を潰すためです。

全生徒に効く指示は2つのテンプレートを、
その生徒だけの指示はcharacter.mdを編集して生成し直します。

2つの届け先はどちらもファイル名が決まっているため、
渡すのはテンプレートのディレクトリと出力先のディレクトリです。
1度の起動でSKILL.mdとMODEL.mdの両方が書き出され、
`nix fmt`も1回にまとまります。

```console
dotnet run --project src/BluePrompt -- roleplay skill --character '<キャラクター名>' \
  --template plugins/role-play \
  --appellation plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json \
  --output <スキルのディレクトリ>
```

character.mdと2つのテンプレートとMODEL.mdは配布物から除かれます。
Claude Codeのプラグインも、
OpenCodeのスキルも、
配布ZIPも、
これらを除いた実体を指します。
生成の入力と別の届け先向けの本文をスキルとして読ませる意味が無く、
特にMODEL.mdはSKILL.mdとほぼ同じ内容なので、
配るとスキルのディレクトリへ人格の指示が二重に置かれた状態になるためです。
除外する名前はflake.nixの`nonSkillFileNames`が持っていて、
漏れは統合チェックが検出します。

抽出設定を調整する時は、
以下でpandoc変換前の中間HTMLを確認できます。
こちらは`nix fmt`を実行しません。

```console
dotnet run --project src/BluePrompt -- wikiru html --page '<ページ名>' --output <出力ファイル>
```

生徒個別ページは折りたたみを残すなど抽出設定が異なるため、
生徒個別ページの設定での中間HTMLは以下で確認します。
こちらも`nix fmt`を実行しません。

```console
dotnet run --project src/BluePrompt -- wikiru student-html --page '<生徒のページ名>' --output <出力ファイル>
```

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

MODEL.md(無ければSKILL.md)と本文から明示的にリンクされた参照ファイルをインライン化して、
システムプロンプトへ焼き込んだワークスペースModelの作成フォームJSONへ変換します。
生成物は`POST /api/v1/models/create`へそのまま渡して登録できる形式です。

人格のスキル分をまとめて生成するのは以下です。
出力はビルド成果物でリポジトリへはコミットしないため、
`nix fmt`は実行しません。

```console
nix build .#open-webui-model
```

スキル1つ分を単体で変換したい時は以下を使います。

```console
dotnet run --project src/BluePrompt -- open-webui model --skill <スキルディレクトリ> --output <出力ファイル>
```

## Knowledge定義の生成

Knowledgeは登録したファイルをチャンクへ割ってベクトル検索するため、
大きなファイルをそのまま渡すと表や節の途中で千切れて検索に掛からなくなります。
SKILL.mdとリンクされたMarkdownの参照ファイルを見出しの単位へ分割して、
検索でヒットする単位と読ませたい単位を揃えます。
断片には祖先の見出しが前置され、
出典の行が全ての断片へ配られるので、
単体で読んでも何の一部なのかが分かります。
ファイル名もコレクションの名前から始まります。
検索で当たった断片は本文とファイル名だけがLLMへ渡るため、
衣装違いの生徒のように似た構造のコレクションが並んでも、
これで出所を区別できます。

`appellation.json`のようなMarkdown以外の参照ファイルは、
jqやスクリプトで引くためのものでOpen WebUIには実行する主体がいないため、
Knowledgeには含めません。

ナレッジのスキル分をまとめて生成するのは以下です。
出力はビルド成果物でリポジトリへはコミットしないため、
`nix fmt`は実行しません。

```console
nix build .#open-webui-knowledge
```

スキル1つ分を単体で変換したい時は以下を使います。

```console
dotnet run --project src/BluePrompt -- open-webui knowledge --skill <スキルディレクトリ> --output <出力ディレクトリ>
```

## ModelとKnowledgeの紐付け

role-playスキルのフロントマターの`knowledge:`行に、
参照するKnowledgeコレクションの名前をカンマ区切りで並べます。
yuukaのように生成するスキルでは、
スキルのディレクトリのcharacter.md側へ書いて生成し直します。

コレクションのidは登録先のインスタンスが採番するため生成時には決まりません。
生成物では空にしておき、
`open-webui sync`が名前でコレクションを引き当てて埋めます。
紐付け先が同期の対象に無ければ、
参照が外れたModelを黙って登録せずエラーで止まります。

紐付けたKnowledgeを自動で参照させるには、
`params.function_calling`を`legacy`(UIの表記では「Default」)にする必要があります。
Open WebUIはv0.10.0以降この既定値が`native`で、
`native`ではモデルがビルトインツールを能動的に呼ばない限りKnowledgeが注入されません。
紐付けのあるModelにはこの指定が自動で入ります。

## 同期

生成したModelとKnowledgeは`open-webui sync`サブコマンドで対象インスタンスへ同期できます。
APIで登録済みのものと突き合わせて、
無ければ作成し、差分があれば上書きし、差分が無ければ書き込みません。
Knowledgeのファイルは`meta.file_hash`(生バイト列のSHA-256)で比較するので、
中身が変わっていないファイルには触りません。

```console
dotnet run --project src/BluePrompt -- open-webui sync --model <モデル定義ディレクトリ> --base-url <ベースURL> \
  [--base-model-id <id>] [--api-key-file <パス>] \
  [--knowledge <ディレクトリ>] [--rag-template-file <パス>]
```

## RAGプロンプトテンプレート

Knowledgeが自動で参照される時、
インスタンス全体のRAGテンプレートがシステムプロンプトの後ろへ連結されます。
Open WebUIの既定値は引用番号の付け方やXMLタグの扱いを英語で指示する内容で、
ロールプレイ用のModelでは人格と話し方を壊します。

そのため`modules/open-webui-rag-template.txt`の差し障りのないテンプレートを、
`--rag-template-file`で`POST /api/v1/retrieval/config/update`へ同期します。
環境変数`RAG_TEMPLATE`は初回起動時にしかDBへ取り込まれず後から変えられないため、
API経由で同期しています。

## NixOSモジュール

この同期を宣言的に行うNixOSモジュールが、
`modules/nixos.nix`にあり`nixosModules.default`として公開されています。
エンドポイントのURLなど登録先に依存する情報だけをオプションで入力すると、
oneshotのsystemdサービスが起動時と生成物の変更時に同期を実行します。
ModelとKnowledgeとRAGテンプレートはいずれも既定で同期の対象です。
モジュールの検証はnix-fast-buildのchecks(`nixos-module`)に含まれています。

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
文字列で書くと閉じタグの対応と入れ子の見通しの悪さで読み書きが難しくなるためです。

Falco.Markupは属性値も`Text.raw`の本文もエスケープしないので、
書いた通りのHTMLが出ます。
wikiruが返すマークアップをそのまま置きたいので本文には`Text.raw`を使い、
エスケープする`Text.enc`は使いません。
`&amp;`のような実体参照も、実物に合わせてそのまま書きます。

要素1つで書けるものは`Elem`と`Attr`と`Text`をそのまま呼びます。
`Elem.td [] [ Text.raw "a" ]`程度の短さのために、
ライブラリと紛らわしい名前のヘルパーを作りません。

`test/BluePrompt.Test/HtmlFixture.fs`に置くのは、
文書全体の組み立てのように要素を並べるだけでは書けないものと、
`<br class="spacer">`のようにwikiru固有の決まったマークアップとして繰り返し現れるものです。
後者は要素1つでも、その値自体に意味があるので名前を付けます。

出力されたHTMLの文字列を検査する側は、
タグの途中や属性の断片も見るのでリテラルのままにします。
