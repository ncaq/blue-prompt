# 使い捨てのOpen WebUIをsystemd-nspawnのコンテナで立ち上げて、
# 生成したModelとKnowledgeを実際に同期するNixOSテスト。
#
# 同期はOpen WebUIのAPIの応答の形と挙動に依存していて、
# 模したサーバでは版が上がった時の変化を捕まえられない。
# 権限の設定が更新で残ることのように、
# 実物でなければ確かめられない約束もここで確かめる。
#
# 手元で素早く回すのは`dotnet test`の単体テストで、
# このテストは重いのでCIのnix-fast-buildで確かめる。
{
  lib,
  pkgs,
  nixosModule,
  blue-prompt,
  open-webui,
}:
let
  url = "http://localhost:8080";

  # 埋め込みのモデル。
  # Knowledgeへファイルを足すとOpen WebUIはその場で埋め込みを計算するため、
  # 埋め込みのモデルが無いとファイルの追加そのものが失敗する。
  # テストのサンドボックスの中からはHugging Face Hubへ取りに行けないので、
  # sentence-transformersが自身のテストに使う17MBのモデルを固定出力で取ってきて置く。
  # 検索の精度はこのテストの関心ではないので、どれだけ小さくても構わない。
  embeddingModel =
    let
      revision = "f3cb857cba53019a20df283396bcca179cf051a4";
      files = {
        "config.json" = "sha256-lZwgVdhPYKnX+pieHrKJIbc58xPi92Vpp0ra5vHAbgg=";
        "config_sentence_transformers.json" = "sha256-kYmSGK8d2WnoXjgrQlXKvxkaPjcPWTlRgUIwRrRhOnc=";
        "modules.json" = "sha256-j0smS4AgbIML673K43fhN5JWUKQztok0OmO9ybMUVGA=";
        "sentence_bert_config.json" = "sha256-7I4p1ty2G2EbfT/dKYLEUk5q2YWVn6cZTqz7ZVqNDVE=";
        "special_tokens_map.json" = "sha256-ttNGvjZqfR1IMy28n987+JYLXYeVIrd5ndulnnYjfuM=";
        "tokenizer.json" = "sha256-kfHe+bk5H9q+AozT8/zE79NOXR8Iw78t5RPrtZEaGFQ=";
        "tokenizer_config.json" = "sha256-rlfto0o9Tju6te3TDFt+TuPEk/pIwuGvFEO2vWGa/Bk=";
        "vocab.txt" = "sha256-B+ztN1zsFE0nyQAkHz4zlHjeyVj5L928VR8pXJkgOKM=";
        "model.safetensors" = "sha256-tXOA2EZctFaBlxarkroSkz+OkUKuX5MLoYyoMOkzOvI=";
        "1_Pooling/config.json" = "sha256-QH12/zmdFTU7ETWxqu5Mv/bn/VgAUdhC1HSCNH/Lt50=";
      };
    in
    pkgs.linkFarm "stsb-bert-tiny-safetensors" (
      lib.mapAttrsToList (path: hash: {
        name = path;
        path = pkgs.fetchurl {
          url = "https://huggingface.co/sentence-transformers-testing/stsb-bert-tiny-safetensors/resolve/${revision}/${path}";
          inherit hash;
        };
      }) files
    );

  # `open-webui model`が書き出すのと同じ形のModelForm。
  modelForm =
    {
      id,
      system,
      knowledge ? null,
    }:
    {
      inherit id;
      base_model_id = null;
      name = id;
      meta = {
        description = "${id}の説明";
        knowledge =
          if knowledge == null then
            null
          else
            map (name: {
              id = null;
              inherit name;
              type = "collection";
            }) knowledge;
      };
      params = {
        inherit system;
        function_calling = if knowledge == null then null else "legacy";
      };
      is_active = true;
    };

  # `open-webui model`の出力と同じく、ModelFormのJSONを並べたディレクトリ。
  modelsDirectory =
    name: forms:
    pkgs.linkFarm name (
      map (form: {
        name = "${form.id}.json";
        path = pkgs.writeText "${form.id}.json" (builtins.toJSON form);
      }) forms
    );

  # `open-webui knowledge`の出力と同じく、
  # コレクションごとのディレクトリにフォームのJSONと登録するファイルを置いたディレクトリ。
  knowledgeDirectory =
    name: collections:
    pkgs.linkFarm name (
      lib.concatLists (
        lib.mapAttrsToList (
          collection:
          { description, files }:
          [
            {
              name = "${collection}/knowledge.json";
              path = pkgs.writeText "knowledge.json" (
                builtins.toJSON {
                  name = collection;
                  inherit description;
                }
              );
            }
          ]
          ++ lib.mapAttrsToList (fileName: content: {
            name = "${collection}/files/${fileName}";
            path = pkgs.writeText fileName content;
          }) files
        ) collections
      )
    );

  # 一覧のAPIは1ページに30件しか返さないため、
  # ページを辿らないと取りこぼす数のファイルを持たせる。
  appellationFiles =
    count:
    lib.listToAttrs (
      lib.genList (index: {
        name = "file${lib.fixedWidthNumber 2 index}.md";
        value = "呼称${toString index}";
      }) count
    );

  # 最初に同期する内容。NixOSモジュールのサービスが同期する。
  initialModels = modelsDirectory "initial-models" [
    (modelForm {
      id = "yuuka";
      system = "古いプロンプト";
      knowledge = [
        "character-appellation"
        "character-yuuka"
      ];
    })
    (modelForm {
      id = "kotori";
      system = "プロンプト";
    })
  ];

  initialCollections = {
    character-appellation = {
      description = "呼称表";
      files = appellationFiles 35;
    };
    character-yuuka = {
      description = "ユウカ";
      files = {
        "a.md" = "ユウカA";
        "b.md" = "ユウカB";
      };
    };
  };

  # スキルを改良した後の内容。
  # プロンプトと説明を変え、ファイルを1つ書き換えて1つ消す。
  improvedModels = modelsDirectory "improved-models" [
    (modelForm {
      id = "yuuka";
      system = "改良したプロンプト";
      knowledge = [
        "character-appellation"
        "character-yuuka"
      ];
    })
    (modelForm {
      id = "kotori";
      system = "プロンプト";
    })
  ];

  improvedCollections = {
    character-appellation = {
      description = "呼称表";
      files = appellationFiles 34 // {
        "file00.md" = "書き換えた呼称0";
      };
    };
    character-yuuka = {
      description = "書き換えたユウカ";
      files = {
        "a.md" = "ユウカA";
        "b.md" = "ユウカB";
      };
    };
  };

  ragTemplate = builtins.readFile ../modules/open-webui-rag-template.txt;
  ragTemplateFile = pkgs.writeText "rag-template" ragTemplate;

  initialKnowledge = knowledgeDirectory "initial-knowledge" initialCollections;
  improvedKnowledge = knowledgeDirectory "improved-knowledge" improvedCollections;

  # 同期した後の登録先が持つべきファイル。テストのスクリプトへJSONとして渡す。
  # 生成物へ渡したのと同じ値から導いて、期待値を二重に書かない。
  filesOf = lib.mapAttrs (_: collection: collection.files);

  expected = {
    initial = filesOf initialCollections;
    improved = filesOf improvedCollections;
  };
in
{
  name = "open-webui-sync";

  containers.machine = {
    imports = [ nixosModule ];

    services.open-webui = {
      enable = true;
      package = open-webui;
      # 既定の待ち受けはIPv4のループバックだけなので、
      # localhostがIPv4とIPv6のどちらへ解決されても繋がるように両方で待ち受ける。
      host = "::";
      environment = {
        # 同期先の運用と同じく認証を無効にして、
        # 同期コマンドが自分でサインインしてトークンを得る経路を通す。
        WEBUI_AUTH = "False";
        # サンドボックスの中にはネットワークが無いので、外へ取りに行かせない。
        OFFLINE_MODE = "true";
        RAG_EMBEDDING_MODEL = "${embeddingModel}";
      };
    };

    blue-prompt.open-webui = {
      enable = true;
      inherit url;
      models = initialModels;
      knowledge = initialKnowledge;
    };

    # テストのスクリプトはホスト側のドライバで動き、コンテナの中のポートへは直接届かない。
    # 準備と確かめのためにAPIを叩くのは、コンテナの中のコマンドとして行う。
    environment.systemPackages = [ pkgs.curl ];
  };

  testScript = /* python */ ''
    import hashlib
    import json
    import shlex

    url = "${url}"
    expected = json.loads(${builtins.toJSON (builtins.toJSON expected)})
    rag_template = ${builtins.toJSON ragTemplate}

    machine.start()
    machine.wait_for_unit("open-webui.service")
    machine.wait_for_open_port(8080, timeout=600)

    # NixOSモジュールのサービスが、起動を待ってから最初の同期を済ませる。
    # 結果を待たないType=execのユニットなので、終わるまで状態を見て待つ。
    machine.wait_until_succeeds(
        "systemctl show --property=SubState --value blue-prompt-open-webui-sync"
        " | grep --line-regexp --extended-regexp 'exited|failed'",
        timeout=900,
    )
    machine.succeed("systemctl is-active blue-prompt-open-webui-sync")
    journal = machine.succeed("journalctl --unit=blue-prompt-open-webui-sync --output=cat")
    for line in ["yuuka: 作成", "kotori: 作成", "character-appellation: 作成", "RAGテンプレート: 更新"]:
        assert line in journal, f"{line}が同期の出力にありません: {journal}"

    # 認証を無効にしたインスタンスのadminとしてトークンを得る。
    token = json.loads(
        machine.succeed(
            f"curl --fail-with-body --silent --show-error {url}/api/v1/auths/signin"
            " --header 'Content-Type: application/json'"
            """ --data '{"email":"admin@localhost","password":"admin"}'"""
        )
    )["token"]


    def api(method, path, body=None):
        command = (
            f"curl --fail-with-body --silent --show-error --request {method} {url}{path}"
            f" --header 'Authorization: Bearer {token}'"
            " --header 'Content-Type: application/json'"
        )
        if body is not None:
            command += " --data " + shlex.quote(json.dumps(body))
        return json.loads(machine.succeed(command))


    def model(id):
        return api("GET", f"/api/v1/models/model?id={id}")


    def grants_of(model):
        return [(grant["principal_type"], grant["principal_id"], grant["permission"]) for grant in model["access_grants"]]


    def collections():
        return {collection["name"]: collection for collection in api("GET", "/api/v1/knowledge/")["items"]}


    def files(collection_id):
        items = []
        page = 1
        while True:
            response = api("GET", f"/api/v1/knowledge/{collection_id}/files?page={page}")
            items += response["items"]
            if not response["items"] or response["total"] <= len(items):
                return items
            page += 1


    def assert_files(state):
        """登録されたファイルの名前と中身が、生成物と一致していることを確かめる。"""
        by_name = collections()
        for collection, desired in expected[state].items():
            registered = files(by_name[collection]["id"])
            names = sorted(file["meta"]["name"] for file in registered)
            assert names == sorted(desired), f"{collection}のファイルが食い違っています: {names}"
            for file in registered:
                content = desired[file["meta"]["name"]].encode()
                assert file["meta"]["file_hash"] == hashlib.sha256(content).hexdigest(), (
                    f"{collection}の{file['meta']['name']}の中身が食い違っています"
                )


    def sync(models, knowledge, *options):
        return machine.succeed(
            f"${lib.getExe blue-prompt} open-webui sync --model {models} --knowledge {knowledge}"
            f" --rag-template-file ${ragTemplateFile} --base-url {url} " + " ".join(options)
        )


    with subtest("最初の同期で全て登録されて紐付けにidが埋まる"):
        assert_files("initial")
        by_name = collections()
        yuuka = model("yuuka")
        assert [reference["id"] for reference in yuuka["meta"]["knowledge"]] == [
            by_name["character-appellation"]["id"],
            by_name["character-yuuka"]["id"],
        ], yuuka["meta"]["knowledge"]
        # 紐付けたKnowledgeを自動で参照させるための方式が入る。
        assert yuuka["params"]["function_calling"] == "legacy", yuuka["params"]
        # 紐付けの無いModelでは、このリポジトリと関係のない設定を上書きしない。
        assert model("kotori")["params"].get("function_calling") is None
        assert api("GET", "/api/v1/retrieval/config")["RAG_TEMPLATE"] == rag_template

    with subtest("同じ内容での再実行では何も書き込まない"):
        output = sync("${initialModels}", "${initialKnowledge}")
        for line in [
            "yuuka: 変更なし",
            "kotori: 変更なし",
            "character-appellation: ファイルの変更なし",
            "character-yuuka: ファイルの変更なし",
            "RAGテンプレート: 変更なし",
        ]:
            assert line in output, f"{line}が同期の出力にありません: {output}"

    with subtest("更新しても管理対象ではない設定が残る"):
        # 登録後にUIで上流モデルと公開範囲を選んだ状況を作る。
        selected = model("yuuka")
        selected["base_model_id"] = "upstream-model"
        api("POST", "/api/v1/models/model/update", selected)
        api(
            "POST",
            "/api/v1/models/model/access/update",
            {
                "id": "yuuka",
                # 全ての利用者が読める、UIの公開の設定。
                # principal_typeが`anyone`の権限は、Modelの経路ではadminが付けても落とされる。
                "access_grants": [{"principal_type": "user", "principal_id": "*", "permission": "read"}],
            },
        )
        # 同期の後に残っているかを見る前に、権限が付いたことを確かめておく。
        assert grants_of(model("yuuka")) == [("user", "*", "read")], model("yuuka")

        # 以前の同期が中断して同じ名前のファイルが残った、壊れた状態を作る。
        appellation_id = collections()["character-appellation"]["id"]
        machine.succeed("echo 中断で残った呼称 > /tmp/file01.md")
        uploaded = json.loads(
            machine.succeed(
                f"curl --fail-with-body --silent --show-error {url}/api/v1/files/?process_in_background=false"
                f" --header 'Authorization: Bearer {token}' --form file=@/tmp/file01.md"
            )
        )
        api("POST", f"/api/v1/knowledge/{appellation_id}/file/add", {"file_id": uploaded["id"]})

        output = sync("${improvedModels}", "${improvedKnowledge}")
        assert "yuuka: 更新" in output, output
        assert "kotori: 変更なし" in output, output
        assert "character-yuuka: 説明を更新" in output, output

        yuuka = model("yuuka")
        assert yuuka["params"]["system"] == "改良したプロンプト", yuuka["params"]
        # base-model-idを指定しない同期は、UIで選ばれた上流モデルを保つ。
        assert yuuka["base_model_id"] == "upstream-model", yuuka
        # 更新のボディに載せない権限の設定は、登録先にそのまま残る。
        assert grants_of(yuuka) == [("user", "*", "read")], yuuka

        assert collections()["character-yuuka"]["description"] == "書き換えたユウカ"
        # 書き換えたファイルは中身が入れ替わり、消したファイルと重複した余りは取り除かれる。
        assert_files("improved")

    with subtest("base-model-idを指定すると上流モデルが上書きされる"):
        output = sync("${improvedModels}", "${improvedKnowledge}", "--base-model-id", "specified-model")
        assert "yuuka: 更新" in output, output
        assert model("yuuka")["base_model_id"] == "specified-model"
        assert model("kotori")["base_model_id"] == "specified-model"
  '';
}
