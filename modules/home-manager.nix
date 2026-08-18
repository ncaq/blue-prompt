# blue-promptのプラグインとスキルをhome-manager経由でAIコーディングアシスタントへ接続するモジュール。
# `plugins`にはプラグイン名からプラグインディレクトリへの辞書を、
# `skills`にはスキル名からスキルディレクトリへの辞書を渡す。
# flake.nixが導出した一覧をそのまま受け取ることで、
# プラグインやスキルを追加してもこのモジュールへの追記は必要なく、
# 接続漏れも起きない。
#
# プラグインはMarkdownのみで構成されておりビルドを必要としないため、
# パッケージではなくソースのディレクトリをそのまま渡す。
# home-managerのclaude-codeとopencodeのモジュールはどちらもパスを受け付けるので、
# systemに依存しない評価が出来て別アーキテクチャ向けの構成でも問題なく扱える。
{ plugins, skills }:
{
  lib,
  options,
  config,
  ...
}:
let
  cfg = config.blue-prompt;
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
      # スキルは`~/.config/opencode/skills/<skill>/`へそれぞれsymlinkされる。
      programs.opencode.skills = skills;
    })
  ];
}
