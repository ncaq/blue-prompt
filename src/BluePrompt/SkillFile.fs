/// スキルのディレクトリで名前が決まっているファイル。
/// 同じ名前をModelを組み立てるOpenWebuiと、
/// Knowledgeを組み立てるOpenWebuiKnowledgeと、
/// 本文を生成するRolePlayが別々に必要とするため、ここを唯一の出どころにする。
///
/// 配布物から除く名前の一覧はflake.nixのnonSkillNamesが別に持っている。
/// Nixへ定数を渡す手段が無いので重複は仕組み上残る。
/// どちらかの名前を変える時はもう片方も直す。
module BluePrompt.SkillFile

/// スキル本体。Claude CodeとOpenCodeが読む。
let skill: string = "SKILL.md"

/// Open WebUIのModel向けの本文。
/// スキル本体と同じ内容を、
/// 参照ファイルがインライン化され、ナレッジが自動で渡される前提の書き方で持つ。
/// open-webui modelはこれがあればスキル本体より優先する。
let model: string = "MODEL.md"

/// 生徒に固有の手書きの部分を書くファイル。
/// 本文を生成するための入力で、フロントマターと本文を持つ。
let character: string = "character.md"

/// 代表的な発言を1つ1つのMarkdownとして置くディレクトリ。
/// 本文を生成するための入力で、生徒ごとに0個以上を手で書く。
/// 衣装別の参照ファイルと同じ階層へ置くと、
/// 除外を並べる形で参照ファイルを拾うRolePlayが衣装として読んでしまうため、
/// 1つ下のディレクトリへ分ける。
let quote: string = "quote"

/// スキル本体の骨格になる、全生徒で共通のテンプレート。
let skillTemplate: string = "SKILL.template.md"

/// Open WebUIのModel向けの本文の骨格になる、全生徒で共通のテンプレート。
let modelTemplate: string = "MODEL.template.md"
