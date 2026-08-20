# blue-prompt

LLMにブルーアーカイブを扱わせる時に役に立つリポジトリ。
それを目指しています。

## 想定利用環境

### LLMサービス

メンテナーの @ncaq が有料部分をそれなりに利用できるLLMサービスは2026年1月24日現在は以下の通りです。

- Claude
- GitHub Copilot Chat
- Gemini

その中でもClaudeが @ncaq の環境では一番使えるようになっているので、
主にClaudeを対象に動作確認を行っています。

しかし自然言語のデータが多めなので、
普通のLLMでもある程度は使えると思います。

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

扱うのはゲーム内で確認できる事実で、
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
- `blue-prompt.opencode.enable`は各プラグインのスキルをフラットに展開して`programs.opencode.skills`へ追加します

`programs.claude-code`や`programs.opencode`自体の有効化や設定は、
通常通りhome-manager側で行ってください。
有効化されていない場合はassertionでエラーになります。

## Claude.aiのweb版

Claude.aiにはZIPファイルでスキルをアップロードします。
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
SKILL.mdと本文から明示的にリンクされた参照ファイルをインライン化して、
システムプロンプトへ焼き込んだワークスペースModelの作成フォームJSONへ変換します。

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

なお`character-appellation`のような巨大な参照ファイルを持つスキルも一応変換されますが、
全キャラクターの呼称表がシステムプロンプトへ丸ごと入るため、
コンテキスト消費が大きく実用には向きません。

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
    url = "http://192.168.0.10:8080";
    # 実際に推論へ使う上流モデル。
    # 省略すると新規作成時はnullになり、登録後にUIで選んだ値を保持します。
    baseModelId = "qwen3:32b";
    # 認証を有効にしているインスタンスではAPIキーのファイルを指定します。
    # systemdのcredentialとして実行時に読むためNix storeへは入りません。
    # apiKeyFile = "/run/secrets/open-webui-api-key";
  };
}
```

oneshotのsystemdサービスが、
起動時とスキルの変更時にAPIで登録済みのModelと突き合わせて、
無ければ作成し、
差分があれば更新し、
差分が無ければ何も書き込みません。
繰り返し実行しても結果は同じです。

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

動作しないと断定できる情報も無いので、
単に未確認の状態です。

# 著作権と扱う範囲

このリポジトリは非公式であり、
『ブルーアーカイブ』の権利者とは一切関係がありません。

『ブルーアーカイブ』に関する権利は
NEXON Games Co., Ltd. および Yostar, Inc. に帰属します。

各リソースに共通する方針をここに書きます。
情報源ごとの事情は、
それぞれのリソースのREADME.mdに書いてあります。

## 扱うのはゲーム内で確認できる事実

記録するのは、
ゲームを遊べば誰でも確認できる事実です。

生徒の名前、
所属、
ステータスの数値、
スキルの効果、
実装日といったものです。
事実それ自体は著作物ではなく、
誰が書いても同じ表現になる部分に著作権は発生しません。

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
