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

## モバイルアプリ

[公式ドキュメント](https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview)
がスキルの動作環境として挙げているのは、
Claude API、Claude Code、Claude.aiのweb版の3つで、
iOSとAndroidのアプリについては記載がありません。

動作しないと断定できる情報も無いので、
単に未確認の状態です。
