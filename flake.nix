{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";
    flake-parts.url = "github:hercules-ci/flake-parts";
    treefmt-nix = {
      url = "github:numtide/treefmt-nix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
    # home-managerモジュールの検証にのみ使用する。
    home-manager = {
      url = "github:nix-community/home-manager/release-26.05";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    inputs@{
      nixpkgs,
      flake-parts,
      treefmt-nix,
      ...
    }:
    let
      inherit (nixpkgs) lib;

      dirNamesIn =
        path: lib.attrNames (lib.filterAttrs (_name: type: type == "directory") (builtins.readDir path));

      pluginDirOf = pluginName: ./plugins + "/${pluginName}";

      # plugins/配下の実ディレクトリからプラグイン一覧を導出する。
      # プラグインやスキルを追加してもここに一覧を追記する必要がなく、
      # 配布物やhome-managerモジュールからの漏れも起きない。
      # system非依存なのでmkFlakeの外で導出し、
      # perSystemとhome-managerモジュールが同じ一覧を共有する。
      pluginNames = dirNamesIn ./plugins;

      # 各プラグインのskills/配下をプラグイン名とスキル名の組で列挙する。
      skills = lib.concatMap (
        pluginName:
        let
          skillsDir = pluginDirOf pluginName + "/skills";
        in
        map (skillName: { inherit pluginName skillName; }) (
          lib.optionals (builtins.pathExists skillsDir) (dirNamesIn skillsDir)
        )
      ) pluginNames;

      # リポジトリにはあるが、スキルとしては配布しないファイルの名前。
      # character.mdは本文を生成するための入力で、
      # *.template.mdは全生徒で共通の骨格、
      # MODEL.mdはOpen WebUIのModel向けの本文なので、
      # Claude CodeやOpenCodeのスキルとして読ませる意味が無い。
      # 特にMODEL.mdはSKILL.mdとほぼ同じ内容なので、
      # 配るとスキルのディレクトリに人格の指示が二重に置かれた状態になる。
      #
      # F#側で同じ名前を持つのはsrc/BluePrompt/SkillFile.fsで、
      # Nixへ定数を渡す手段が無いので重複は仕組み上残る。
      # どちらかの名前を変える時はもう片方も直す。
      nonSkillFileNames = [
        "character.md"
        "MODEL.md"
        "MODEL.template.md"
        "SKILL.template.md"
      ];

      # 配布しないファイルを除いたディレクトリ。
      # ビルドを挟まず評価時のコピーだけで済ませるため、
      # プラグインをそのまま渡していた時と同じくsystemに依存せず扱える。
      distributable =
        name: path:
        builtins.path {
          inherit name path;
          filter = entry: _type: !(lib.elem (baseNameOf entry) nonSkillFileNames);
        };

      # プラグインごとの、Open WebUIでの登録先。
      #
      # Open WebUIのModelは会話の入口を選ぶだけで、
      # Claude Codeのスキルのように会話の途中で他のModelを読み込むことはできない。
      # そのため人格を与えるスキルはModelにする意味があるが、
      # 参照して事実を引くだけのスキルはModelにしても選ばれず読まれない。
      # 後者はKnowledgeコレクションとして登録して、
      # 人格側のModelから紐付けたりチャットで`#`を打って引いたりできるようにする。
      openWebuiKinds = {
        role-play = "model";
        jp-wikiru-bluearchive = "knowledge";
      };

      # 分類の追記漏れで、追加したプラグインが黙ってどちらにも登録されない状態を防ぐ。
      openWebuiSkillsOf =
        kind:
        assert lib.assertMsg (lib.sort lib.lessThan (lib.attrNames openWebuiKinds) == pluginNames) ''
          openWebuiKindsのプラグイン一覧がplugins/のディレクトリ一覧と一致しません。
          openWebuiKinds: ${toString (lib.attrNames openWebuiKinds)}
          plugins/: ${toString pluginNames}'';
        lib.filter (skill: openWebuiKinds.${skill.pluginName} == kind) skills;

      # プラグイン名からプラグインディレクトリへの辞書。
      pluginPaths = lib.genAttrs pluginNames (
        pluginName: distributable pluginName (pluginDirOf pluginName)
      );

      # スキル名からスキルディレクトリへの辞書。
      # OpenCodeはプラグインの単位を持たないためスキルをフラットに展開する必要があるが、
      # 複数プラグインが同名のスキルを持つと片方が黙って消えるため、
      # 衝突を評価時に検出する。
      skillPaths =
        let
          skillOwners = lib.mapAttrs (_skillName: map (skill: skill.pluginName)) (
            lib.groupBy (skill: skill.skillName) skills
          );
          skillNameConflicts = lib.filterAttrs (_skillName: owners: 1 < lib.length owners) skillOwners;
        in
        assert lib.assertMsg (
          skillNameConflicts == { }
        ) "blue-promptのプラグイン間でスキル名が衝突しています: ${builtins.toJSON skillNameConflicts}";
        lib.listToAttrs (
          map (
            { pluginName, skillName }:
            lib.nameValuePair skillName (
              distributable skillName (pluginDirOf pluginName + "/skills/${skillName}")
            )
          ) skills
        );
    in
    flake-parts.lib.mkFlake { inherit inputs; } {
      imports = [
        treefmt-nix.flakeModule
      ];

      systems = [ "x86_64-linux" ];

      flake = {
        # プラグインとスキル一式をClaude CodeやOpenCodeへ接続するhome-managerモジュール。
        homeModules.default = import ./modules/home-manager.nix {
          plugins = pluginPaths;
          skills = skillPaths;
        };

        # スキルから生成したOpen WebUIのワークスペースModelとKnowledgeを、
        # 対象インスタンスへ宣言的に同期するNixOSモジュール。
        # 同期コマンドと生成物はsystemに依存するため、
        # モジュール側でホストのsystemに応じて解決する。
        nixosModules.default = import ./modules/nixos.nix {
          packagesFor = system: inputs.self.packages.${system};
        };
      };

      perSystem =
        {
          lib,
          pkgs,
          ...
        }:
        let
          # marketplace.jsonの`metadata.version`はリポジトリ全体の配布バージョンとして扱う。
          # プラグイン個別のバージョンはそれぞれのplugin.jsonが持つ。
          # この配布物は全プラグインのスキルをまとめたものなので前者を名前に使う。
          marketplace = lib.importJSON ./.claude-plugin/marketplace.json;

          # F#アナライザーのCLIツール。
          # nixpkgsに存在しないためNuGetから直接パッケージ化する。
          # MSBuildのAnalyzeFSharpProjectターゲットは`dotnet fsharp-analyzers`を起動し、
          # それはdotnet CLIのツール解決でPATH上の`dotnet-fsharp-analyzers`に解決されるため、
          # そのコマンド名の別名も一緒に置く。
          fsharp-analyzers = pkgs.buildDotnetGlobalTool {
            pname = "fsharp-analyzers";
            version = "0.37.2";
            nugetHash = "sha256-tbQYxXQ39bXmvFQo9CL4A91RK5IHi3P6poUSc+C8448=";
            dotnet-sdk = pkgs.dotnet-sdk_10;
            dotnet-runtime = pkgs.dotnet-sdk_10;
            # net8.0向けに配布されているツールをSDK 10のランタイムで動かす。
            makeWrapperArgs = [
              "--set-default"
              "DOTNET_ROLL_FORWARD"
              "Major"
            ];
            # binのラッパーはfixupフェーズのdotnetFixupHookが作るため、
            # 別名のリンクはその後に張る。
            postFixup = ''
              ln -s $out/bin/fsharp-analyzers $out/bin/dotnet-fsharp-analyzers
            '';
          };

          blue-prompt = pkgs.buildDotnetModule {
            pname = "blue-prompt";
            # 外部に配布しないプログラムなのでバージョン番号に意味が無い。
            version = "0.0.0";
            # plugins/配下のMarkdown変更でF#の再ビルドが走らないようにソースを限定する。
            src = lib.fileset.toSource {
              root = ./.;
              fileset = lib.fileset.unions [
                # アナライザーの参照はDirectory.Build.propsで全プロジェクトへ注入しているため、
                # restoreの整合性のためにソースへ含める。
                ./Directory.Build.props
                ./Directory.Build.targets
                # リント対象のプロジェクトをglobで集約する単一エントリポイント。
                ./lint.proj
                ./src
                ./test
              ];
            };
            # BluePrompt.Analyzersはリント専用の自作アナライザーで配布物ではないが、
            # NuGet依存をdeps.jsonへ含めてサンドボックス内でビルドできるようにするため、
            # projectFileの一覧に含める。
            projectFile = [
              "src/BluePrompt/BluePrompt.fsproj"
              "src/BluePrompt.Analyzers/BluePrompt.Analyzers.fsproj"
            ];
            testProjectFile = "test/BluePrompt.Test/BluePrompt.Test.fsproj";
            nugetDeps = ./deps.json;
            dotnet-sdk = pkgs.dotnet-sdk_10;
            dotnet-runtime = pkgs.dotnetCorePackages.runtime_10_0;
            doCheck = true;
            # サンドボックス内では外部ネットワークへ出られないため、外部サイト依存テストは除外する。
            testFilters = [ "Category!=Network" ];
            # HTML→Markdown変換のテストがpandocを起動するため、check環境に含める。
            nativeCheckInputs = [
              pkgs.pandoc
              fsharp-analyzers
            ];
            # F#アナライザーによるリントもテストに続けて実行する。
            # nix-fast-buildの統合チェックにリンターを含めるため。
            # -warnaserrorで警告も失敗へ昇格して違反を検出する。
            # Configurationにはビルドと同じ構成(既定はRelease)を指定し、
            # Directory.Build.targetsがCLIのconfigurationフラグへ引き渡すことで、
            # 解析対象を実際に配布・テストされる構成へ揃える。
            # buildPhaseの出力はRuntimeIdentifier付きの別パスへ出るため再利用はされず、
            # 自作アナライザーのRID無しビルドが1回走る。
            # 並列数はnixpkgsのdotnetフック群と同じくNixが割り当てたコア数へ揃えて、
            # 複数derivationの並行ビルド時のCPUの過剰割り当てを避ける。
            postCheck = ''
              dotnet msbuild lint.proj /t:AnalyzeFSharpProject -warnaserror \
                -maxcpucount:"$NIX_BUILD_CORES" -p:Configuration="$dotnetBuildType"
            '';
            executables = [ "BluePrompt" ];
            # nix runで単体実行できるようにpandocのパスをラッパーに焼き込む。
            makeWrapperArgs = [
              "--set-default"
              "PANDOC_PATH"
              (lib.getExe pkgs.pandoc)
            ];
            meta.mainProgram = "BluePrompt";
          };

          # NuGet依存のロックファイル更新を一回の実行で完結させる。
          # fetch-depsはネットワークを使うためNixサンドボックス外で実行する必要があり、
          # nix runで起動するスクリプトとして提供する。
          update-deps = pkgs.writeShellApplication {
            name = "update-deps";
            runtimeInputs = [
              pkgs.git
              pkgs.nix
            ];
            text = ''
              cd "$(git rev-parse --show-toplevel)"
              ${blue-prompt.passthru.fetch-deps} deps.json
              # 生成されたJSONをリポジトリのフォーマット規約(prettier)に揃える。
              nix fmt deps.json
              git add deps.json
              if git diff --cached --quiet -- deps.json; then
                echo "deps.jsonに変更はありません"
              else
                git commit --message "build: NuGet依存のロックファイルdeps.jsonを更新" -- deps.json
              fi
            '';
          };

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
                    cp -r ${skillPaths.${skillName}} ${workDir}/${skillName}
                    chmod -R u+w ${workDir}
                    # nix storeのタイムスタンプとファイル列挙順に依存しないアーカイブにする。
                    find ${workDir} -exec touch -d "@$SOURCE_DATE_EPOCH" {} +
                    # -Xはuid/gidなどの環境依存の拡張属性を格納しないオプション。
                    (cd ${workDir} && find ${skillName} | sort | zip --quiet -X $out/${workDir}.zip -@)
                  ''
                ) skills}
              '';
          # Open WebUIにはスキルのようなオンデマンド読み込みの仕組みが無いため、
          # SKILL.mdと参照ファイルをインライン化したシステムプロンプトを持つ、
          # ワークスペースModelの作成フォームJSONを人格のスキルごとに生成する。
          # POST /api/v1/models/createへそのまま渡して登録できる。
          open-webui-model = pkgs.runCommand "open-webui-model-${marketplace.metadata.version}" { } ''
            # dotnetランタイムがユーザプロファイルへ書き込もうとするため、
            # サンドボックス内でも書けるHOMEを用意する。
            export HOME="$TMPDIR"
            mkdir -p $out
            ${lib.concatMapStrings (
              { pluginName, skillName }:
              # claude-ai-skillと同様に、
              # プラグインを跨いだスキル名の重複で出力が衝突しないように、
              # 出力ファイル名はプラグイン名で名前空間に分ける。
              ''
                ${lib.getExe blue-prompt} open-webui-model \
                  ${./plugins + "/${pluginName}/skills/${skillName}"} \
                  $out/${pluginName}-${skillName}.json
              ''
            ) (openWebuiSkillsOf "model")}
          '';

          # 参照して事実を引くスキルは、
          # 検索でヒットする単位と読ませたい単位が揃うように見出しの単位へ分割して、
          # Knowledgeコレクションの定義一式として生成する。
          open-webui-knowledge = pkgs.runCommand "open-webui-knowledge-${marketplace.metadata.version}" { } ''
            export HOME="$TMPDIR"
            mkdir -p $out
            ${lib.concatMapStrings (
              { pluginName, skillName }:
              ''
                ${lib.getExe blue-prompt} open-webui-knowledge \
                  ${./plugins + "/${pluginName}/skills/${skillName}"} \
                  $out/${pluginName}-${skillName}
              ''
            ) (openWebuiSkillsOf "knowledge")}
          '';
        in
        {
          treefmt.config = {
            projectRootFile = "flake.nix";
            programs = {
              actionlint.enable = true;
              deadnix.enable = true;
              fantomas = {
                enable = true;
                # デフォルトはpkgs.dotnet-sdk(SDK 8)なのでプロジェクトと同じSDK 10に揃える。
                dotnet-sdk = pkgs.dotnet-sdk_10;
              };
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
            package-blue-prompt = blue-prompt;
            package-claude-ai-skill = claude-ai-skill;
            package-open-webui-model = open-webui-model;
            package-open-webui-knowledge = open-webui-knowledge;

            # home-managerモジュールを実際のhome-manager構成へ組み込んで、
            # プラグインとスキルが実際に接続されていることを検証する。
            # 接続が空になっても評価自体は通り続けるため、
            # 内容を検証しないとモジュールが実質何も接続しなくなった退行を検出できない。
            home-manager-module =
              let
                homeConfiguration = inputs.home-manager.lib.homeManagerConfiguration {
                  inherit pkgs;
                  modules = [
                    inputs.self.homeModules.default
                    {
                      home = {
                        username = "blue-prompt-test";
                        homeDirectory = "/home/blue-prompt-test";
                        stateVersion = "26.05";
                      };
                      programs = {
                        claude-code.enable = true;
                        opencode.enable = true;
                      };
                      blue-prompt = {
                        claude-code.enable = true;
                        opencode.enable = true;
                      };
                    }
                  ];
                };
                claudeCodePlugins = homeConfiguration.config.programs.claude-code.plugins;
                # モジュールがhome-managerのバージョンに応じてリストと属性セットを使い分けるため、
                # どちらで接続されても中身を取り出せるようにリストへ揃える。
                pluginPathList =
                  if lib.isList claudeCodePlugins then claudeCodePlugins else lib.attrValues claudeCodePlugins;
                inherit (homeConfiguration.config.programs.opencode) skills;
              in
              # Claude Code本体はunfreeでビルドにライセンス許諾の設定が必要なため、
              # 構成全体(activationPackage)ではなく接続先の実体だけをビルド時に検証する。
              assert lib.assertMsg (
                pluginPathList != [ ]
              ) "blue-promptのプラグインがprograms.claude-code.pluginsへ接続されていません";
              assert lib.assertMsg (skills ? kotori) "blue-promptのスキルがprograms.opencode.skillsへ展開されていません";
              pkgs.runCommand "home-manager-module" { } ''
                # Claude Code側: 接続されたプラグインがマニフェストを持つ実体である。
                ${lib.concatMapStrings (pluginPath: ''
                  test -f ${pluginPath}/.claude-plugin/plugin.json
                '') pluginPathList}
                # OpenCode側: 代表スキルの本体が接続されている。
                test -f ${skills.kotori}/SKILL.md
                # 生成の入力や別の届け先向けの本文が、どちらへも混ざっていない。
                ${
                  let
                    assertClean = path: ''
                      if [ -n "$(find ${path} \( ${
                        lib.concatMapStringsSep " -o " (name: "-name ${name}") nonSkillFileNames
                      } \) -print -quit)" ]; then
                        echo "配布しないファイルが混ざっています: ${path}" >&2
                        exit 1
                      fi
                    '';
                  in
                  lib.concatMapStrings assertClean (pluginPathList ++ lib.attrValues skills)
                }
                touch $out
              '';

            # NixOSモジュールを実際のNixOS構成へ組み込んで、
            # 同期サービスがスクリプトの実体ごと構成されることを検証する。
            # スクリプトにはモデル定義のディレクトリが焼き込まれているため、
            # この検証は生成物のビルドまで含めて通す。
            nixos-module =
              let
                nixosConfiguration = inputs.nixpkgs.lib.nixosSystem {
                  inherit (pkgs.stdenv.hostPlatform) system;
                  modules = [
                    inputs.self.nixosModules.default
                    {
                      system.stateVersion = "26.05";
                      blue-prompt.open-webui = {
                        enable = true;
                        url = "http://127.0.0.1:8080";
                        baseModelId = "test-model";
                        apiKeyFile = "/run/secrets/open-webui-api-key";
                      };
                    }
                  ];
                };
                inherit (nixosConfiguration.config.systemd.services.blue-prompt-open-webui-sync.serviceConfig)
                  ExecStart
                  ;
              in
              # ブートローダなどを持たない最小構成のため、
              # システム全体(toplevel)ではなくExecStartのコマンドラインだけを検証する。
              # ExecStartには同期コマンドと生成物のstoreパスが含まれるため、
              # このファイル経由の参照で全てのビルドまで検証される。
              pkgs.runCommand "nixos-module" { } ''
                execStartFile=${pkgs.writeText "blue-prompt-open-webui-sync-exec-start" ExecStart}
                grep -- open-webui-sync "$execStartFile"
                # ナレッジとRAGテンプレートも既定で同期の対象になっている。
                grep -- --knowledge "$execStartFile"
                grep -- --rag-template-file "$execStartFile"
                # APIキーはsystemdが実行時に展開するcredentialのパスで渡される。
                grep -- '$'{CREDENTIALS_DIRECTORY}/api-key "$execStartFile"
                # コマンドラインの先頭は実行可能な同期コマンドの実体を指している。
                # escapeSystemdExecArgsが引数をダブルクォートで包むため外して読む。
                program=$(cut --delimiter ' ' --fields 1 "$execStartFile" | tr --delete '"')
                test -x "$program"
                touch $out
              '';
          };

          packages = {
            # flake.lockの管理バージョンをre-exportすることで安定した利用を促進。
            inherit (pkgs)
              nix-fast-build
              ;

            inherit
              blue-prompt
              claude-ai-skill
              open-webui-knowledge
              open-webui-model
              update-deps
              ;
          };

          devShells.default = pkgs.mkShell {
            buildInputs = with pkgs; [
              # treefmtで指定したプログラムの単体版。
              actionlint
              deadnix
              editorconfig-checker
              fantomas
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

              # F#開発ツール。
              dotnet-sdk_10
              fsautocomplete
              fsharp-analyzers

              # HTML→Markdown変換。
              pandoc
            ];
            env = {
              DOTNET_ROOT = "${pkgs.dotnet-sdk_10}/share/dotnet";
              # 初回実行時のウェルカムバナーやロゴ出力を抑止してコマンド出力を綺麗に保つ。
              DOTNET_NOLOGO = "1";
              # HTML→Markdown変換に使うpandocをnixpkgsのもので固定する。
              PANDOC_PATH = lib.getExe pkgs.pandoc;
            };
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
