---
name: character-appellation
description: Lookup how Blue Archive characters address themselves and each other, including first-person pronouns, nicknames and honorifics. Use when writing dialogue, role-playing a character, or checking what a character calls another character or themselves.
---

『ブルーアーカイブ』のキャラクターが、
自分や他のキャラクターをどう呼ぶかを調べるためのスキルです。

一人称、
あだ名、
敬称の有無といった呼び方は捏造されやすい知識なので、
記憶で答えずにこの表を引いてください。

# データの引き方

呼称の一覧は同じディレクトリの[reference.md](./reference.md)にあります。
[キャラ呼称表 - ブルーアーカイブ(ブルアカ)攻略有志Wiki](https://bluearchive.wikiru.jp/?%E3%82%AD%E3%83%A3%E3%83%A9%E5%91%BC%E7%A7%B0%E8%A1%A8)
を機械的に整形したものです。

ファイルが大きいので全体を読み込まず、
Grepで`#### キャラクター名`を検索して該当セクションの周辺だけをReadしてください。

# reference.mdの構造

- 見出しは学校(h2) > 部活・組織(h3) > キャラクター(h4)の階層です。部活の無い所属では部活の見出しを挟まずキャラクターが続きます
- 各キャラクターのテーブルは「キャラクター・相手・呼称」の3列です
- 相手が「自分」の行はそのキャラクターの一人称です
- 1つのセルに複数の呼称がある場合は「、」で区切られています
- 呼称や相手の直後の半角括弧()には「初対面時」「変装中」など、その呼称を使う条件や補足が書いてあります

# appellation.json

同じディレクトリの[appellation.json](./appellation.json)は、
reference.mdと同じ内容の機械読み出し用のレコード集です。
jqやスクリプトで機械的に抽出したい時はこちらを使ってください。

トップレベルは`{"source": <出典URL>, "entries": [レコードの配列]}`で、
レコードは`.entries`配下にあります。

```console
jq '.entries[] | select(.caller == "ユウカ")' appellation.json
```

reference.mdで1つのセルに「、」区切りで並ぶ複数の呼称は、
JSONでは呼称ごとに1レコードへ分かれます。

各レコードのフィールドは以下の通りです。

- `school`: 呼ぶ側の所属する学校
- `club`: 呼ぶ側の所属する部活・組織。学校直下の所属ではnull
- `caller`: 呼ぶ側のキャラクター名
- `callee`: 呼ばれる相手。「自分」は一人称を表す
- `calleeNote`: 相手の名前に付いた補足。無ければnull
- `name`: 呼称
- `note`: 「初対面時」のような呼称を使う条件や補足。無ければnull

# 使う時の注意

- ある呼び方が表に無い場合、勝手に補完せず「呼称は確認できていない」として扱ってください。似た名前のキャラクターの呼称を混ぜないでください
- 呼ぶ側のキャラクターを演じる時は、括弧の条件も含めて場面に合った呼称を選んでください
