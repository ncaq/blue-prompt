# role-play

ゲーム『ブルーアーカイブ』のキャラクターとして振る舞わせるスキル集です。

このスキル集が生まれた経緯と導入方法は、
リポジトリルートの[README.md](../../README.md)に書いてあります。

## 収録スキル

| スキル   | キャラクター | 所属                                      |
| -------- | ------------ | ----------------------------------------- |
| `yuuka`  | 早瀬ユウカ   | ミレニアムサイエンススクール セミナー     |
| `kotori` | 豊見コトリ   | ミレニアムサイエンススクール エンジニア部 |
| `seia`   | 百合園セイア | トリニティ総合学園 ティーパーティー       |

スキル名は名字を含めない名前のみにしています。
作中で生徒が名字で呼ばれることはほとんどなく、
名字を思い出せなくてもスキルを呼び出せるようにするためです。

## 衣装ごとの参照ファイル

`yuuka`スキルにはSKILL.mdの他に、
衣装(実装)ごとのプロフィールとゲーム内ボイス一覧を収めた参照ファイルがあります。

これらは
[ブルーアーカイブ(ブルアカ)攻略有志Wiki](https://bluearchive.wikiru.jp/)
の生徒個別ページからの自動生成ファイルです。
手で編集せず、リポジトリルートで以下のコマンドで再生成してください。

```console
dotnet run --project src/BluePrompt -- wikiru-roleplay-reference 'ユウカ' plugins/role-play/skills/yuuka/normal.md
dotnet run --project src/BluePrompt -- wikiru-roleplay-reference 'ユウカ（体操服）' plugins/role-play/skills/yuuka/track.md
dotnet run --project src/BluePrompt -- wikiru-roleplay-reference 'ユウカ（パジャマ）' plugins/role-play/skills/yuuka/pajama.md
```

## SKILL.mdの生成

`yuuka`スキルのSKILL.mdは、
手書きのテンプレートSKILL.template.mdからの自動生成ファイルです。

キャラクターが誰をどう呼ぶかの呼称表は没入感を大きく左右するため、
別ファイルへ分けずスキル本体へ直接埋め込む方針で、
テンプレートのプレースホルダ`{{appellation}}`へ、
jp-wikiru-bluearchiveプラグインに同梱の生成済みappellation.jsonから抜き出した呼称表を流し込みます。

人格や口調の指示を変えたい時はテンプレートを編集して、
以下のコマンドでSKILL.mdを生成し直してください。
SKILL.mdを直接編集してはいけません。
テンプレートは出力先と同じディレクトリのSKILL.template.mdが使われます。
wikiruへはアクセスしません。
呼称表そのものを更新したい時は先に`wikiru-appellation`で再生成してください。

```console
dotnet run --project src/BluePrompt -- roleplay-skill 'ユウカ' plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json plugins/role-play/skills/yuuka/SKILL.md
```

`kotori`と`seia`はまだこの構成に移行しておらず、
SKILL.mdに手で貼り付けたデータのままです。

## 注意

スタイルはチャット全体に常時適用されるものでしたが、
スキルはClaudeが必要と判断した時に読み込まれるものです。
そのため各スキルには人格を維持するための指示を含めていますが、
スタイルと完全に同じ挙動になるわけではありません。
