# blue-promptのスキルから生成したOpen WebUIのワークスペースModel定義を、
# 対象インスタンスへ宣言的に同期するNixOSモジュール。
# `packagesFor`にはsystemからこのflakeのpackagesへの関数を渡す。
# flake.nixが`blue-prompt`と`open-webui-model`をそのまま接続することで、
# スキルを追加してもこのモジュールへの追記は必要ない。
#
# 同期はoneshotのsystemdサービスがBluePromptのopen-webui-syncサブコマンドで行う。
# Open WebUIのModelはDBに保存される状態なのでNixだけでは宣言できず、
# APIで突き合わせて足りない分だけ書き込むことで冪等にする。
# ユニットにはモデル定義のディレクトリが焼き込まれているため、
# スキルを改良して構成を切り替えるとユニットが変わって同期が再実行され、
# 登録済みのModelが自動で上書きされる。
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
    enable = lib.mkEnableOption "syncing blue-prompt skills to Open WebUI workspace models";

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
        既定では全スキル分を同期するので、
        一部だけ登録したい時は選別したディレクトリで上書きする。
      '';
    };
  };

  config = lib.mkIf cfg.enable {
    systemd.services.blue-prompt-open-webui-model-sync = {
      description = "Sync blue-prompt skills to Open WebUI workspace models";
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
          )
          + lib.optionalString (cfg.apiKeyFile != null) " --api-key-file \${CREDENTIALS_DIRECTORY}/api-key";
        LoadCredential = lib.optional (cfg.apiKeyFile != null) "api-key:${cfg.apiKeyFile}";
      };
    };
  };
}
