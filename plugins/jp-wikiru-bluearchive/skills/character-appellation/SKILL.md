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

- 見出しは学校(h2) > 部活・組織(h3) > キャラクター(h4)の階層です
- 各キャラクターのテーブルは「キャラクター・相手・呼称」の3列です
- 相手が「自分」の行はそのキャラクターの一人称です
- 1つのセルに複数の呼称がある場合は「、」で区切られています
- `[^1]`形式の脚注に「初対面時」「変装中」など呼称を使う条件が書いてあり、定義はファイル末尾にあります

# 使う時の注意

- ある呼び方が表に無い場合、勝手に補完せず「呼称は確認できていない」として扱ってください。似た名前のキャラクターの呼称を混ぜないでください
- 呼ぶ側のキャラクターを演じる時は、脚注の条件も含めて場面に合った呼称を選んでください
