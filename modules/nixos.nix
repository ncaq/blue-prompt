# blue-promptのスキルから生成したOpen WebUIのワークスペースModel定義と、
# Knowledgeコレクションの定義を、
# 対象インスタンスへ宣言的に同期するNixOSモジュール。
# `packagesFor`にはsystemからこのflakeのpackagesへの関数を渡す。
# flake.nixが`blue-prompt`と生成物をそのまま接続することで、
# スキルを追加してもこのモジュールへの追記は必要ない。
#
# 同期はoneshotのsystemdサービスがBluePromptのopen-webui-syncサブコマンドで行う。
# Open WebUIのModelはDBに保存される状態なのでNixだけでは宣言できず、
# APIで突き合わせて足りない分だけ書き込むことで冪等にする。
# ユニットには生成物のディレクトリが焼き込まれているため、
# スキルを改良して構成を切り替えるとユニットが変わって同期が再実行され、
# 登録済みのModelとKnowledgeが自動で上書きされる。
#
# エンドポイントのURLや推論へ使う上流モデルのような、
# 登録先のインスタンスに依存してこのリポジトリからは知り得ない情報だけを、
# オプションとして呼び出し側から入力する。
{ packagesFor }:
{
  lib,
  pkgs,
  config,
  utils,
  ...
}:
let
  cfg = config.blue-prompt.open-webui;

  packages = packagesFor pkgs.stdenv.hostPlatform.system;
in
{
  options.blue-prompt.open-webui = {
    enable = lib.mkEnableOption "syncing blue-prompt skills to Open WebUI models and knowledge";

    url = lib.mkOption {
      type = lib.types.str;
      example = "http://192.168.0.10:8080";
      description = ''
        同期先のOpen WebUIのベースURL。
        このホストから到達できるアドレスを指定する。
      '';
    };

    baseModelId = lib.mkOption {
      type = lib.types.nullOr lib.types.str;
      default = null;
      example = "qwen3:32b";
      description = ''
        各Modelの`base_model_id`へ設定する、実際に推論へ使う上流モデル。
        nullの場合は新規作成時はnullのままにして、
        既存のModelでは登録後にUIで選ばれた値を保持する。
      '';
    };

    apiKeyFile = lib.mkOption {
      # パスリテラルを渡すと文字列化の時点で秘密がNix storeへコピーされてしまうため、
      # 文字列型にしてstoreへの取り込みを構造的に不可能にする。
      type = lib.types.nullOr lib.types.str;
      default = null;
      example = "/run/secrets/open-webui-api-key";
      description = ''
        Open WebUIのAPIキーを格納したファイルのパス。
        storeパスではなく実行時に存在する絶対パスを文字列で指定する。
        systemdのcredentialとして実行時に読むためNix storeへは入らない。
        `WEBUI_AUTH`を無効化したインスタンスへ同期する場合はnullのままで良い。
        Open WebUIは認証を無効にしてもAPIにはトークンを要求するが、
        その場合はUIが裏で行っているのと同じサインインでトークンを得る。
      '';
    };

    models = lib.mkOption {
      type = lib.types.path;
      default = packages.open-webui-model;
      defaultText = lib.literalExpression "blue-prompt.packages.\${system}.open-webui-model";
      description = ''
        同期するModelFormのJSON群を含むディレクトリ。
        既定では人格を与えるスキル分を全て同期するので、
        一部だけ登録したい時は選別したディレクトリで上書きする。
      '';
    };

    knowledge = lib.mkOption {
      type = lib.types.nullOr lib.types.path;
      default = packages.open-webui-knowledge;
      defaultText = lib.literalExpression "blue-prompt.packages.\${system}.open-webui-knowledge";
      description = ''
        同期するKnowledgeコレクションの定義を含むディレクトリ。
        nullにするとKnowledgeを同期しないが、
        Modelがナレッジを参照していると紐付け先が見つからず同期が失敗する。
      '';
    };

    ragTemplate = lib.mkOption {
      type = lib.types.nullOr lib.types.lines;
      default = builtins.readFile ./open-webui-rag-template.txt;
      defaultText = lib.literalExpression "builtins.readFile ./open-webui-rag-template.txt";
      description = ''
        インスタンス全体で使うRAGのプロンプトテンプレート。

        Modelへ紐付けたKnowledgeが自動で参照される時、
        このテンプレートがシステムプロンプトの後ろへ連結される。
        Open WebUIの既定値は引用番号の付け方やXMLタグの扱いを英語で指示する内容で、
        ロールプレイ用のModelでは人格と話し方を壊すため、
        既定では同梱の差し障りのないテンプレートへ差し替える。

        nullにするとインスタンスの設定に手を触れない。
      '';
    };
  };

  config = lib.mkIf cfg.enable {
    systemd.services.blue-prompt-open-webui-sync = {
      description = "Sync blue-prompt skills to Open WebUI models and knowledge";
      wantedBy = [ "multi-user.target" ];
      after = [ "network-online.target" ];
      wants = [ "network-online.target" ];
      serviceConfig = {
        Type = "oneshot";
        RemainAfterExit = true;
        DynamicUser = true;
        # ExecStartはシェルを経由しないため、
        # エスケープにはsystemdの構文に合わせたescapeSystemdExecArgsを使う。
        # credentialのパスはsystemdが実行時に環境変数として展開する必要があり、
        # エスケープすると`$`が潰れてしまうため、エスケープ対象外として末尾へ連結する。
        # credentialのディレクトリパスに空白は含まれないので、そのまま1引数になる。
        ExecStart =
          utils.escapeSystemdExecArgs (
            [
              (lib.getExe packages.blue-prompt)
              "open-webui-sync"
              "${cfg.models}"
              cfg.url
            ]
            ++ lib.optionals (cfg.baseModelId != null) [
              "--base-model-id"
              cfg.baseModelId
            ]
            ++ lib.optionals (cfg.knowledge != null) [
              "--knowledge"
              "${cfg.knowledge}"
            ]
            ++ lib.optionals (cfg.ragTemplate != null) [
              "--rag-template-file"
              "${pkgs.writeText "blue-prompt-open-webui-rag-template" cfg.ragTemplate}"
            ]
          )
          + lib.optionalString (cfg.apiKeyFile != null) " --api-key-file \${CREDENTIALS_DIRECTORY}/api-key";
        LoadCredential = lib.optional (cfg.apiKeyFile != null) "api-key:${cfg.apiKeyFile}";
      };
    };
  };
}
