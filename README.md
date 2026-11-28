# blue-prompt

LLMにブルーアーカイブを扱わせる時に役に立つリポジトリ。
それを目指しています。

## 想定利用環境

### LLMサービス

Claudeがメンテナの @ncaq の環境では一番使えるようになっているので、
Claudeを対象に動作確認を行っています。

またローカルLLMを動かしやすい、
[open-webui](https://github.com/open-webui/open-webui)でも、
カスタムモデルとして使えるようにしています。

しかし自然言語のデータが多めなので、
フォーマットさえ変換すれば、
普通のLLMサービスならある程度は使えると思います。

## 典型的な使い方

ClaudeのProjectを作成して、
このGitHubリポジトリをファイルとして追加して、
必要なリソースを選択することを想定しています。

必要なリソースはその時次第で選択してください。

[スキル](https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview)
として提供しているリソースは、
Claude CodeとClaude.aiのweb版にそれぞれ導入することもできます。

# 利用できるリソース

## [plugins/role-play](./plugins/role-play)

『ブルーアーカイブ』のキャラクターとして振る舞わせるスキル集です。

もともとは[Claudeのカスタムスタイル](https://support.claude.com/ja/articles/10181068-%E3%82%B9%E3%82%BF%E3%82%A4%E3%83%AB%E3%81%AE%E8%A8%AD%E5%AE%9A%E3%81%A8%E4%BD%BF%E7%94%A8)
向けに書いていたものですが、
スタイル機能が廃止されてスキルへの移行が案内されたため、
スキルとして書き直しました。

## [plugins/jp-wikiru-bluearchive](./plugins/jp-wikiru-bluearchive)

[ブルーアーカイブ(ブルアカ)攻略有志Wiki](https://bluearchive.wikiru.jp/)
の情報を、
LLMがナレッジベースとして参照しやすい形に整えるためのプラグインです。

LLMはブルーアーカイブについてそれらしい嘘をつくため、
記憶ではなく出典のある事実を引かせることを狙っています。

扱うのはゲーム内で確認できる事実と、
ゲーム内外の事実をまとめた小ネタで、
wiki独自の解説や考察はそのままの形では置きません。
構成したナレッジはリポジトリに置き、
同じページを何度も取得しに行かずに済むようにします。
詳しくは[プラグインのREADME.md](./plugins/jp-wikiru-bluearchive/README.md)を参照してください。

# スキルの導入方法

## Claude Code

このリポジトリ自体がマーケットプレイスになっています。

```console
/plugin marketplace add ncaq/blue-prompt
/plugin install role-play@blue-prompt
```

## home-manager

`homeModules.default`をimportすると、
プラグインとスキル一式をClaude CodeやOpenCodeへ接続できます。

flakeのinputに追加します。

```nix
{
  inputs.blue-prompt.url = "github:ncaq/blue-prompt";
}
```

home-managerの構成に組み込みます。

```nix
{
  imports = [ inputs.blue-prompt.homeModules.default ];

  blue-prompt = {
    claude-code.enable = true;
    opencode.enable = true;
  };
}
```

- `blue-prompt.claude-code.enable`は全プラグインを`programs.claude-code.plugins`へ追加します
- `blue-prompt.opencode.enable`は各プラグインのスキルをフラットに展開して`~/.config/opencode/skills/blue-prompt/`へまとめて置きます。
  OpenCodeはプラグインの単位を持たず、
  キャラクターの呼び名そのままのrole-playのスキル名は他のマーケットプレイスのスキル名と衝突しうるため、
  role-playのスキル名だけ`role-play-himari`のようにプラグイン名をprefixにした名前になります。
  スキルを`programs.opencode.skills`へ1つずつ登録すると数百のスキルの分だけ評価が重くなるため、
  このオプションは使わずディレクトリを1つリンクします。
  `programs.opencode.skills`へ属性セットで登録した他のスキルとは併用でき、
  名前が重なった時はassertionでエラーになります

`programs.claude-code`や`programs.opencode`自体の有効化や設定は、
通常通りhome-manager側で行ってください。
有効化されていない場合はassertionでエラーになります。

## Claude.aiのweb版

### マーケットプレイスから導入する

Claude.aiのweb版もプラグインのマーケットプレイスに対応しているため、
Claude Codeと同じくこのリポジトリをそのまま追加できます。
生成物を作ってアップロードする手間が無いので、
基本的にはこちらを使ってください。

- 左のサイドバーの`Customize`を開く
- `Plugins`タブを開く
- `Personal plugins`の`+`から`Add marketplace`を選ぶ
- `Add from a repository`を選んで`https://github.com/ncaq/blue-prompt`を入力する

追加すると`role-play`と`jp-wikiru-bluearchive`が一覧に出るので、
使いたいプラグインを個別に有効化してください。

リポジトリ側の更新はマーケットプレイスから同期されるので、
スキルを改良するたびにZIPを作り直してアップロードし直す必要はありません。

[プラグインは有料プラン(Pro, Max, Team, Enterprise)の機能です](https://support.claude.com/en/articles/13837440-use-plugins-in-claude)。

### ZIPファイルをアップロードする

マーケットプレイスを使わず、
スキル単体をZIPファイルでアップロードすることもできます。
アップロードするファイルをNixで生成できます。
cloneしていなくても以下のコマンドで生成できます。

```console
nix build github:ncaq/blue-prompt#claude-ai-skill
ls result
```

cloneしている場合はリポジトリのルートで以下を実行してください。

```console
nix build .#claude-ai-skill
```

生成されるZIPファイルは`<プラグイン名>-<スキル名>.zip`の形式で、
スキルごとに1つずつ作られます。
利用したいキャラクターのファイルを個別にアップロードしてください。

アップロードは、
`Settings`の`Capabilities`で`Code execution and file creation`を有効にした上で、
`Customize`の`Skills`から行います。

## Open WebUI

[Open WebUI](https://github.com/open-webui/open-webui)には、
Claude Codeのスキルのような指示書と参照ファイルの組をオンデマンドで読み込む仕組みがありません。
そのため、
SKILL.mdの本文を、
システムプロンプトへ焼き込んだワークスペースModelの作成フォームJSONへ変換します。
読ませたい参照データは本文が節として持っている前提なので、
変換の側では参照ファイルを解決しません。

cloneしていなくても以下のコマンドで生成できます。

```console
nix build github:ncaq/blue-prompt#open-webui-model
ls result
```

cloneしている場合はリポジトリのルートで以下を実行してください。

```console
nix build .#open-webui-model
```

生成されるJSONファイルは`<プラグイン名>-<スキル名>.json`の形式で、
スキルごとに1つずつ作られます。
Open WebUIの`POST /api/v1/models/create`へそのまま渡して登録できます。

```console
curl -X POST "$OPEN_WEBUI_URL/api/v1/models/create" \
  -H "Authorization: Bearer $OPEN_WEBUI_API_KEY" \
  -H "Content-Type: application/json" \
  -d @result/role-play-yuuka.json
```

実際に推論へ使う上流モデルを表す`base_model_id`は、
登録先のインスタンスに依存するため`null`で生成されます。
登録後にワークスペースのモデル編集画面か更新APIで基盤モデルを設定してください。

なお`character-appellation`のような事実を引くスキルは、
参照ファイルへのリンクを本文に持つためエラーで止まります。
Open WebUIには会話の途中でファイルを開く手段が無く、
リンクを残しても開けない参照を読ませるだけだからです。
これらは`nix build .#open-webui-knowledge`で作るKnowledgeコレクションとして登録してください。

### 検証しているRAG設定

Knowledgeコレクションの分割の粒度は、
作者が以下のRAG設定のインスタンスで検索が当たることを実測して決めています。
別の設定でも動きますが、
検索の当たり方はこの構成でしか検証していません。

- 埋め込みモデル: [intfloat/multilingual-e5-large](https://huggingface.co/intfloat/multilingual-e5-large)。
  e5系はprefixを前提に学習されているため、
  `RAG_EMBEDDING_QUERY_PREFIX`に`query: `を、
  `RAG_EMBEDDING_CONTENT_PREFIX`に`passage: `を指定します
- 埋め込みエンジン: `RAG_EMBEDDING_ENGINE`は`ollama`で、GGUFのq8_0量子化。
  ただしエンジンの違いは検索精度に影響しないことを実測で確認しているので、
  既定のsentence-transformersのままモデル名だけ合わせても同じ結果になります
- 分割と検索: `CHUNK_SIZE`は1000、`CHUNK_OVERLAP`は100、`RAG_TOP_K`は3の、
  いずれもOpen WebUIの既定値。ハイブリッド検索とリランカーは使っていません
- ベクタDB: `VECTOR_DB`は`pgvector`。
  既定のChromaDBでの動作は未検証ですが、検索の当たり方が変わる要素ではないはずです

実測の経緯と数値は以下のissueに記録があります。

- エンジンの選定: https://github.com/ncaq/blue-prompt/issues/171
- モデルの選定: https://github.com/ncaq/blue-prompt/issues/196

この設定での登録先の実際の構築は、
作者のdotfilesの https://github.com/ncaq/dotfiles/pull/1550 が行っています。

モデルの乗り換えや、
ハイブリッド検索とリランカーの導入でこの前提が変わる時は、
モデルの選定のissueの測定をやり直した上でこの節を更新します。

### NixOSモジュールによる宣言的な同期

Open WebUIをNixOSで運用している場合は、
`nixosModules.default`をimportすると、
全スキル分のModelを対象インスタンスへ宣言的に同期できます。
エンドポイントのURLのような、
登録先のインスタンスに依存してこのリポジトリからは知り得ない情報だけを入力します。

```nix
{
  imports = [ inputs.blue-prompt.nixosModules.default ];

  blue-prompt.open-webui = {
    enable = true;
    url = "http://open-webui.example.com:8080";
    # 実際に推論へ使う上流モデル。
    # 省略すると新規作成時はnullになり、登録後にUIで選んだ値を保持します。
    baseModelId = "qwen3:32b";
    # 認証を有効にしているインスタンスではAPIキーのファイルを指定します。
    # systemdのcredentialとして実行時に読むためNix storeへは入りません。
    # apiKeyFile = "/run/secrets/open-webui-api-key";
  };
}
```

Open WebUIは`WEBUI_AUTH`を無効にしてもAPIにはBearerトークンを要求するため、
`apiKeyFile`を省略した場合は、
認証無効時のadminとしてサインインしてトークンを取得してから同期します。
UIが裏で行っているのと同じことを自分で行うだけなので、
インスタンス側に追加の設定は要りません。

systemdサービスが、
起動時とスキルの変更時にAPIで登録済みのModelと突き合わせて、
無ければ作成し、
差分があれば更新し、
差分が無ければ何も書き込みません。
繰り返し実行しても結果は同じです。

同期は裏で走り、
boot時の`multi-user.target`の到達や`nixos-rebuild switch`は完了を待ちません。
Knowledgeのアップロードは登録先での埋め込みで長く掛かるためです。
成否は`nixos-rebuild`の出力には出ないので、
`systemctl status blue-prompt-open-webui-sync`とジャーナルで確かめてください。

スキルが改良された時は、
flake inputを更新して構成を切り替えるだけで、
変わったModelが自動で上書きされます。
UIでシステムプロンプトなどを書き換えていても、
スキル側の内容で上書きされます(`base_model_id`の選択は保持されます)。
また更新が発生した時は、
公開範囲やタグのような同期が管理しないUI側の設定も、
まとめてリセットされる可能性があります。

スキル側から消えたModelの削除までは行わないので、
不要になったModelはUIか削除APIで消してください。

## モバイルアプリ

[公式ドキュメント](https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview)
がスキルの動作環境として挙げているのは、
Claude API、Claude Code、Claude.aiのweb版の3つで、
iOSとAndroidのアプリについては記載がありません。

しかしプラグインはアカウントに対して追加されるものなので、
web版でマーケットプレイスからプラグインを追加しておけば、
モバイルアプリからでも使えることを確認しています。

ただしアプリ側にはスキルを選ぶ画面が見当たらないため、
会話の最初に「ユウカのスキルを使って」のように、
使いたいスキルを明示的に指定してください。

# 著作権と扱う範囲

このリポジトリは非公式であり、
『ブルーアーカイブ』の権利者とは一切関係がありません。

『ブルーアーカイブ』に関する権利は
NEXON Games Co., Ltd. および Yostar, Inc. に帰属します。

各リソースに共通する方針をここに書きます。
情報源ごとの事情は、
それぞれのリソースのREADME.mdに書いてあります。

## 扱うのは事実

記録するのは、
事実です。

生徒の名前、
所属、
ステータスの数値、
スキルの効果、
実装日といった、
ゲームを遊べば誰でも確認できるものです。
事実それ自体は著作物ではなく、
誰が書いても同じ表現になる部分に著作権は発生しません。

小ネタは、
ゲーム内の事実と現実世界の事実(武器の元ネタなど)をまとめたものです。
これも事実なので、
ゲーム内の事実と同じ扱いに含めます。

## ストーリーの扱い

台詞やストーリーは、
数値と違って表現そのものです。
ゲーム内で確認できる事実だから自由に扱える、
とは言い切れない領域です。

そこで読むのに何が必要かで線を引きます。

### 誰でも読める部分

メインストーリーやイベントストーリーは、
ゲームを起動すれば誰でも読めます。
費用も条件もかかりません。
話の筋を事実として扱う分には、
それほど神経質になる必要はないと考えています。

ただし本文をそのまま並べることはしません。
権利の問題以前に、
参照するためのナレッジとしてかさばるだけで役に立たないからです。
筋を要約する形で扱います。

### 絆ストーリー

絆ストーリーは扱いが違います。

これは基本的にガチャを回して生徒を獲得しないと読めません。
つまり対価を払った人だけが読める部分です。
無料で読める部分と同じ感覚でそのまま載せるのは、
一線を越えていると考えています。

絆ストーリーについては、
何が語られたかの要約と、
そういう設定があるという程度の記述に留めます。
本文を並べることはしません。

## 台詞の扱い

ロビーや戦闘のボイス一覧のように、
ゲーム内で誰でも確認できてwikiにも整理されている台詞は、
既知の事実としてそのまま扱います。
話し方の癖や一人称や語尾は、
[plugins/role-play](./plugins/role-play)のようなリソースが必要とするものです。
表現を鑑賞させるためではなく、
そのキャラクターがどう喋るかという事実を示すために引きます。

絆ストーリーの台詞は上の線引きの通り別で、
本文を並べず要約と短い例に留めます。

## 要請には従う

権利者や情報源の運営から削除や訂正の要請があった場合は、
速やかに対応します。
連絡先はリポジトリのIssueか、
メールアドレス<ncaq@ncaq.net>までお願いします。

# 開発

以下のコマンドはdevShell(direnvをロードした通常の環境)の中で使います。

## フォーマットと統合チェック

フォーマットとリントは以下で実行できます。

```console
nix fmt
```

このうち綴りの検査(typos)は、`plugins`以下を対象から外しています。
中身の大半がwikiruの記事から生成したファイルで、
「M-LOOK」のような固有の表記を英単語の綴り違いとして書き換えてしまうためです。
`plugins`以下の手で書くファイルの綴りも検査されないので、自分で確かめてください。

統合チェックは以下で実行できます。

```console
nix-fast-build --option eval-cache false --no-link --skip-cached --no-nom
```

checksの`open-webui-sync`は、
使い捨てのOpen WebUIをsystemd-nspawnのコンテナで立ち上げるNixOSテストで、
Nixのデーモンに`uid-range`のsystem-featureを要求します。
設定の無い環境ではこれだけが落ちるので、
NixOSのマニュアルの"Running Tests"の"System Requirements"に従って設定してください。
CIでは`.github/workflows/check.yml`がこの設定を渡しています。

## ビルドとテスト

slnファイルは使っていないため、
プロジェクトのディレクトリを指定して実行します。

```console
dotnet build src/BluePrompt
dotnet test test/BluePrompt.Test
```

## リント

F#のリンターは統合チェックの一部としても実行されます。
単体で実行したい時は以下を使います。

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
`.github/workflows/update-deps.yml`が同じ再生成をRenovateのブランチへコミットします。

## 生成コマンド

生成コマンドを手元のソースから実行する時は、
devShellが置く`blue-prompt`コマンドを使います。
中身は`dotnet run --configuration Release --project src/BluePrompt --`で、
引数はそのまま渡り、相対パスは呼び出した場所が基準になります。
引数無しで起動すると全てのサブコマンドの使い方を表示します。

`plugins/jp-wikiru-bluearchive/`配下のスキルのナレッジと、
`plugins/role-play/`配下のスキルの参照ファイルと本文は生成物なので、
手で編集せずこれらのコマンドで生成し直してください。
`wikiru`と`roleplay`の生成コマンドは、
読んだ構造を確認する`wikiru article`と`wikiru character`を除いて、
書き出した直後に`nix fmt`まで実行するので、
生成コマンドだけで内容が確定します。

### 一括更新

`src/BluePrompt/Manifest.fs`に書いた対象を全てwikiruから取得して書き出し、
続けてrole-playスキルの本文も生成し直してから、
最後に`nix fmt`を1回だけ実行します。
対象のパスはリポジトリのルートからの相対で、`--root`で基準を渡します。

```console
blue-prompt wikiru all --root .
```

取得に失敗した対象があれば、失敗した対象の名前と理由を表示して終了コード1で止まります。
失敗時は整形もrole-playスキルの生成もしないので、
書き出し済みの生成物は整形されないまま残ります。

wikiruへアクセスせずrole-playスキルの本文だけを全て生成し直す時は以下を使います。
テンプレートを直した時と、
統合チェックの`roleplay-generated`が生成物の鮮度を確かめる時に使われます。

```console
blue-prompt roleplay all --root .
```

### マニフェストの漏れの確認

`Manifest.fs`の対象は手で足すため、
wikiruへ生徒や衣装違いが増えても一括更新は何も言いません。
漏れは以下で調べます。
wikiruの一覧を取得して突き合わせるだけで、何も書き出しません。
食い違いが1つでもあれば終了コード1で止まります。

```console
blue-prompt wikiru check-manifest
```

外部サイトの状態で結果が変わるため、統合チェックには含めていません。

### 個別の生成

新しい生徒を足す時は、
個別のコマンドで生成してから、
以後の一括更新に含めるために`Manifest.fs`へも対象を足してください。

一般的なページを整形した`reference.md`は以下で生成します。

```console
blue-prompt wikiru knowledge --page '<ページ名>' --output <出力ファイル>
```

キャラ呼称表のスキルは、
LLM参照用の`reference.md`と機械読み出し用の`appellation.json`を以下で同時に生成します。

```console
blue-prompt wikiru appellation --page 'キャラ呼称表' \
  --markdown-output plugins/jp-wikiru-bluearchive/skills/character-appellation/reference.md \
  --json-output plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json
```

学校別キャラクター一覧のスキルは、
学校ごとの表にまとめた`reference.md`と機械読み出し用の`school.json`を以下で同時に生成します。

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

プレイアブルではない登場人物のスキルは以下で生成します。

```console
blue-prompt wikiru unplayable-skill --page '<ページ名>' --output <SKILL.mdの出力パス>
```

role-playスキルの衣装別参照ファイルは、
生徒個別ページからプロフィールとボイスと小ネタだけを抜き出して以下で生成します。

```console
blue-prompt wikiru roleplay-reference --page '<生徒のページ名>' --output <出力ファイル>
```

role-playスキルの本文は、
`plugins/role-play/SKILL.template.md`へ生徒ごとに決まる値を差し込んで以下で生成します。
wikiruへはアクセスせず、リポジトリへ併置した生成物だけで完結します。

```console
blue-prompt roleplay skill --character '<キャラクター名>' \
  --template plugins/role-play \
  --appellation plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json \
  --output <スキルのディレクトリ>
```

ページの読み方を調整する時は、
以下でMarkdownへ書き出す前の記事の構造をJSONで確認できます。
`nix fmt`は実行しません。

```console
blue-prompt wikiru article --page '<ページ名>' --output <出力ファイル>
```

生徒個別ページは、記事の構造をさらに節ごとの専用の型として読んでいます。
以下でその構造をJSONで確認できます。
`content`の`Case`が`Other`の節は、専用の型で読まずに汎用の構造のまま持っている節です。

```console
blue-prompt wikiru character --page '<生徒のページ名>' --output <出力ファイル>
```

専用の型で読もうとして読めなかった節があると、
生成コマンドはページ名と節と理由を標準エラーへ出します。
その節も汎用の構造のまま書き出されるので、生成物の中身が欠けることはありません。

読み方を直す間に同じページを何度も取りに行かないように、
環境変数`BLUE_PROMPT_HTML_CACHE`へディレクトリを指定すると、
取得したHTMLをそこへ保存して2回目以降はそこから読みます。
wikiの更新を取り込む時は指定しないでください。

```console
BLUE_PROMPT_HTML_CACHE=<キャッシュのディレクトリ> blue-prompt wikiru all --root .
```

### Open WebUI向けの生成と同期

全スキル分のModelとKnowledgeは以下で生成します。
出力はビルド成果物でリポジトリへはコミットしないため、
`nix fmt`は実行しません。

```console
nix build .#open-webui-model
nix build .#open-webui-knowledge
```

スキル1つ分を単体で変換したい時は以下を使います。

```console
blue-prompt open-webui model --skill <スキルディレクトリ> --output <出力ファイル>
blue-prompt open-webui knowledge --skill <スキルディレクトリ> --output <出力ディレクトリ>
```

生成したModelとKnowledgeは以下で対象インスタンスへ同期できます。

```console
blue-prompt open-webui sync --model <モデル定義ディレクトリ> --base-url <ベースURL> \
  [--base-model-id <id>] [--api-key-file <パス>] \
  [--knowledge <ディレクトリ>] [--rag-template-file <パス>]
```
