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
# その結果としてOpenCode側を有効にした評価はsystemに依存する。
# 中身はテキスト処理だけでターゲット依存が無いので、
# cross構成でもエミュレーションやリモートビルダーを要求しないように、
# ビルドはbuildPackages側で実行する。
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
  # 接続は一式をまとめて1つのリンクで行うため、
  # スキルごとのderivationには分けず1つへまとめる。
  #
  # 実際に書き換えるのは各スキルのSKILL.md 1ファイルだけなので、
  # 他のファイルはsymlinkでstoreの実体を指して複製を避ける。
  # runCommandLocalは代替を引かず全マシンでローカル実行されるため、
  # 実体コピーにするとplugins/全体をstoreへもう1組持つことになる。
  #
  # sedの対象は先頭のフロントマターの範囲に限定する。
  # ファイル全体を対象にすると、
  # フロントマターにnameが無いスキルの本文のnameに似た行を書き換えて、
  # 素のスキル名のまま登録される失敗が黙って通るため。
  # 書き換え後にフロントマターへ展開名が実際に入ったことを検査して、
  # nameを持たないスキルもここで止まるようにする。
  opencodeSkills = pkgs.buildPackages.runCommandLocal "blue-prompt-opencode-skills" { } ''
    ${lib.concatStrings (
      lib.mapAttrsToList (flatName: skillDir: ''
        mkdir -p "$out/${flatName}"
        for entry in "${skillDir}"/*; do
          [ "$(basename "$entry")" = SKILL.md ] || ln -s "$entry" "$out/${flatName}/"
        done
        sed '1,/^---$/ s/^name: .*/name: ${flatName}/' "${skillDir}/SKILL.md" > "$out/${flatName}/SKILL.md"
        sed -n '1,/^---$/p' "$out/${flatName}/SKILL.md" | grep -qx 'name: ${flatName}'
      '') skills
    )}
  '';

  opencodeSkillNameConflicts =
    let
      otherSkills = config.programs.opencode.skills;
    in
    lib.optionals (lib.isAttrs otherSkills) (lib.attrNames (lib.intersectAttrs otherSkills skills));
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
        {
          # OpenCodeはフロントマターのnameでスキルを登録するため、
          # 置き場所が違っても同じ名前のスキルは衝突する。
          # `programs.opencode.skills`へ登録していた時はオプションの定義の衝突として検出できていたが、
          # 自前でリンクを張るとその検出から外れるので、
          # 他のモジュールが登録したスキルとの名前の重複をここで検出する。
          # ディレクトリのパスで渡された時は中身が評価時には分からないため検査しない。
          assertion = cfg.opencode.enable -> opencodeSkillNameConflicts == [ ];
          message = "blue-promptのスキルとprograms.opencode.skillsのスキルの名前が衝突しています: ${toString opencodeSkillNameConflicts}";
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
      # スキル一式は`~/.config/opencode/skills/blue-prompt/`へまとめてsymlinkされ、
      # 個々のスキルはその下の`<展開名>/`に並ぶ。
      #
      # `programs.opencode.skills`へスキルを1つずつ登録すると、
      # home-managerがスキルごとに値の種類をpathIsDirectoryで調べてファイルのエントリを作り、
      # 数百のスキルの分だけ評価のたびに時間とメモリを使う。
      # storeパスに対するpathIsDirectoryは評価時ビルド(IFD)にもなる。
      # 自前でリンクを1つ張れば、スキルがいくつあってもエントリは1つで、IFDも起きない。
      # ref https://github.com/ncaq/blue-prompt/issues/252
      #
      # `programs.opencode.skills`へディレクトリのパスとして渡す方法は、
      # 他のモジュールが同じオプションへ属性セットでスキルを足せなくなるため採らない。
      #
      # OpenCodeは`skills/`の下を再帰的に探してSKILL.mdを拾い、
      # 途中のディレクトリがsymlinkでも辿るため、
      # 1段深い場所に置いてもフロントマターのnameでそのまま登録される。
      # OpenCode 2.3.0で実測して確認している。
      xdg.configFile."opencode/skills/blue-prompt".source = opencodeSkills;
    })
  ];
}
