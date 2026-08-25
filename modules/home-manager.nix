# blue-promptのプラグインとスキルをhome-manager経由でAIコーディングアシスタントへ接続するモジュール。
# `plugins`にはプラグイン名からプラグインディレクトリへの辞書を、
# `skills`にはOpenCode向けに決めたフラットな展開名からスキルディレクトリへの辞書を渡す。
# flake.nixが導出した一覧をそのまま受け取ることで、
# プラグインやスキルを追加してもこのモジュールへの追記は必要なく、
# 接続漏れも起きない。
#
# プラグインはMarkdownのみで構成されておりビルドを必要としないため、
# パッケージではなくソースのディレクトリをそのまま渡す。
# home-managerのclaude-codeモジュールはパスを受け付けるので、
# systemに依存しない評価が出来て別アーキテクチャ向けの構成でも問題なく扱える。
# OpenCode側だけはSKILL.mdの書き換えが必要なため、
# 構成先のpkgsでスキル一式をビルドして接続する。
{ plugins, skills }:
{
  lib,
  pkgs,
  options,
  config,
  ...
}:
let
  cfg = config.blue-prompt;

  # OpenCodeはディレクトリ名ではなくSKILL.mdのフロントマターのnameでスキルを登録するため、
  # prefix付きの展開名のディレクトリへ置くだけでは、
  # 素のスキル名のまま登録されて他のマーケットプレイスのスキルと衝突したままになる。
  # フロントマターのnameをディレクトリ名と同じ展開名へ書き換えたスキル一式を構築する。
  # 展開名がスキル名のままのスキルでは書き換えは何も変えないが、
  # 分岐を持ち込むほどのコストではないため一律に通す。
  # home-managerはスキルの値の種類をpathIsDirectoryで判別していて、
  # storeパスに対しては評価時ビルド(IFD)になるため、
  # スキルごとのderivationに分けず1つへまとめてビルドを1回で済ませる。
  #
  # 実際に書き換えるのは各スキルのSKILL.md 1ファイルだけなので、
  # 他のファイルはsymlinkでstoreの実体を指して複製を避ける。
  # runCommandLocalは代替を引かず全マシンでローカル実行されるため、
  # 実体コピーにするとplugins/全体をstoreへもう1組持つことになる。
  opencodeSkills = pkgs.runCommandLocal "blue-prompt-opencode-skills" { } ''
    ${lib.concatStrings (
      lib.mapAttrsToList (flatName: skillDir: ''
        mkdir -p $out/${flatName}
        for entry in ${skillDir}/*; do
          [ "$(basename "$entry")" = SKILL.md ] || ln -s "$entry" $out/${flatName}/
        done
        grep -q '^name: ' ${skillDir}/SKILL.md
        sed '0,/^name: .*/s//name: ${flatName}/' ${skillDir}/SKILL.md > $out/${flatName}/SKILL.md
      '') skills
    )}
  '';
in
{
  options.blue-prompt = {
    claude-code.enable = lib.mkEnableOption "loading blue-prompt plugins into Claude Code";
    opencode.enable = lib.mkEnableOption "loading blue-prompt skills into OpenCode";
  };

  config = lib.mkMerge [
    {
      # blue-prompt側だけを有効にしてもhome-manager側の設定が丸ごと捨てられて、
      # 警告もエラーも出ずに何も起きないため、
      # 起こりやすい設定ミスとして明示的なエラーで検出する。
      assertions = [
        {
          assertion = cfg.claude-code.enable -> config.programs.claude-code.enable;
          message = "blue-prompt.claude-code.enableには`programs.claude-code.enable = true`が必要です";
        }
        {
          assertion = cfg.opencode.enable -> config.programs.opencode.enable;
          message = "blue-prompt.opencode.enableには`programs.opencode.enable = true`が必要です";
        }
      ];
    }
    (lib.mkIf cfg.claude-code.enable {
      # 属性セット型が推奨の新しいhome-managerと、
      # リスト型のみを受け付ける古いhome-managerの両方に対応するため、
      # 属性セットのまま受理されるかをオプションの型に問い合わせて渡す形式を決める。
      programs.claude-code.plugins =
        if options.programs.claude-code.plugins.type.check plugins then plugins else lib.attrValues plugins;
    })
    (lib.mkIf cfg.opencode.enable {
      # OpenCodeはプラグインの単位を持たないため、
      # プラグインを跨いでフラットに展開したスキルを接続する。
      # スキルは`~/.config/opencode/skills/<展開名>/`へそれぞれsymlinkされる。
      programs.opencode.skills = lib.mapAttrs (
        flatName: _skillDir: "${opencodeSkills}/${flatName}"
      ) skills;
    })
  ];
}
