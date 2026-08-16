{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";
    flake-parts.url = "github:hercules-ci/flake-parts";
    treefmt-nix = {
      url = "github:numtide/treefmt-nix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    inputs@{
      flake-parts,
      treefmt-nix,
      ...
    }:
    flake-parts.lib.mkFlake { inherit inputs; } {
      imports = [
        treefmt-nix.flakeModule
      ];

      systems = [ "x86_64-linux" ];

      perSystem =
        {
          lib,
          pkgs,
          ...
        }:
        let
          marketplace = lib.importJSON ./.claude-plugin/marketplace.json;

          dirNamesIn =
            path: lib.attrNames (lib.filterAttrs (_name: type: type == "directory") (builtins.readDir path));

          # plugins/配下の実ディレクトリからプラグイン一覧を導出する。
          # プラグインやスキルを追加してもここに一覧を追記する必要がなく、
          # 配布物からの漏れも起きない。
          pluginNames = dirNamesIn ./plugins;

          # 各プラグインのskills/配下をプラグイン名とスキル名の組で列挙する。
          skills = lib.concatMap (
            pluginName:
            let
              skillsDir = ./plugins + "/${pluginName}/skills";
            in
            map (skillName: { inherit pluginName skillName; }) (
              lib.optionals (builtins.pathExists skillsDir) (dirNamesIn skillsDir)
            )
          ) pluginNames;

          marketplacePluginNames = lib.sort lib.lessThan (map (plugin: plugin.name) marketplace.plugins);

          # Claude.aiのweb版はスキルをZIPファイルでアップロードする形式のため、
          # そのままアップロードできるファイルをスキルごとに生成する。
          # Claude Codeはリポジトリのディレクトリをそのまま読めるので変換は必要ない。
          claude-ai-skill =
            # plugins/のディレクトリ一覧とmarketplace.jsonの登録内容の齟齬を評価時に検出する。
            # 追記漏れの際にどちらに何が足りないかがその場で分かるように、
            # 失敗メッセージには実際の両方の一覧を埋め込む。
            assert lib.assertMsg (marketplacePluginNames == pluginNames) ''
              marketplace.jsonのプラグイン一覧がplugins/のディレクトリ一覧と一致しません。
              marketplace.json: ${toString marketplacePluginNames}
              plugins/: ${toString pluginNames}'';
            pkgs.runCommand "claude-ai-skill-${marketplace.metadata.version}"
              {
                nativeBuildInputs = [ pkgs.zip ];
              }
              ''
                mkdir -p $out
                ${lib.concatMapStrings (
                  { pluginName, skillName }:
                  # プラグインを跨いでスキル名が重複しても衝突しないように、
                  # 作業ディレクトリと出力ファイルの名前をプラグイン名で名前空間に分ける。
                  # Claude Codeもプラグイン配下のスキルを`plugin:skill`の形で参照するため、
                  # プラグイン名を冠する命名はその慣習にも沿う。
                  # ZIP内のルートディレクトリはスキルの`name`と一致させる必要があるため、
                  # そちらはスキル名のままにする。
                  let
                    workDir = "${pluginName}-${skillName}";
                  in
                  ''
                    mkdir -p ${workDir}
                    cp -r ${./plugins + "/${pluginName}/skills/${skillName}"} ${workDir}/${skillName}
                    chmod -R u+w ${workDir}
                    # nix storeのタイムスタンプとファイル列挙順に依存しないアーカイブにする。
                    find ${workDir} -exec touch -d "@$SOURCE_DATE_EPOCH" {} +
                    # -Xはuid/gidなどの環境依存の拡張属性を格納しないオプション。
                    (cd ${workDir} && find ${skillName} | sort | zip --quiet -X $out/${workDir}.zip -@)
                  ''
                ) skills}
              '';
        in
        {
          treefmt.config = {
            projectRootFile = "flake.nix";
            programs = {
              actionlint.enable = true;
              deadnix.enable = true;
              nixfmt.enable = true;
              prettier.enable = true;
              shellcheck.enable = true;
              shfmt.enable = true;
              statix.enable = true;
              typos.enable = true;
              zizmor.enable = true;
            };
            settings.formatter = {
              editorconfig-checker = {
                command = pkgs.editorconfig-checker;
                includes = [ "*" ];
              };
              zizmor.options = [ "--pedantic" ];
            };
          };

          # CIはnix-fast-buildを既定のchecksに対して実行するため、
          # 配布物のビルドもchecksへ露出させて検証対象に含める。
          checks = {
            package-claude-ai-skill = claude-ai-skill;
          };

          packages = {
            # flake.lockの管理バージョンをre-exportすることで安定した利用を促進。
            inherit (pkgs)
              nix-fast-build
              ;

            inherit claude-ai-skill;
          };

          devShells.default = pkgs.mkShell {
            buildInputs = with pkgs; [
              # treefmtで指定したプログラムの単体版。
              actionlint
              deadnix
              editorconfig-checker
              nixfmt
              prettier
              shellcheck
              shfmt
              statix
              typos
              zizmor

              # nixの関連ツール。
              nix-fast-build

              # GitHub関連ツール。
              gh
            ];
          };
        };
    };

  nixConfig = {
    extra-substituters = [
      "https://cache.nixos.org/"
      "https://niks3-public.ncaq.net/"
      "https://ncaq.cachix.org/"
      "https://nix-community.cachix.org/"
    ];
    extra-trusted-public-keys = [
      "cache.nixos.org-1:6NCHdD59X431o0gWypbMrAURkbJ16ZPMQFGspcDShjY="
      "niks3-public.ncaq.net-1:e/B9GomqDchMBmx3IW/TMQDF8sjUCQzEofKhpehXl04="
      "ncaq.cachix.org-1:XF346GXI2n77SB5Yzqwhdfo7r0nFcZBaHsiiMOEljiE="
      "nix-community.cachix.org-1:mB9FSh9qf2dCimDSUo8Zy7bkq5CX+/rkCWyvRCYg3Fs="
    ];
  };
}
