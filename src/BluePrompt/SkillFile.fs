/// スキルのディレクトリで名前が決まっているファイル。
/// 同じ名前をModelを組み立てるOpenWebuiと、
/// Knowledgeを組み立てるOpenWebuiKnowledgeと、
/// 本文を生成するRolePlayが別々に必要とするため、ここを唯一の出どころにする。
///
/// 配布物から除くファイルの一覧はflake.nixのnonSkillFileNamesが別に持っている。
/// Nixへ定数を渡す手段が無いので重複は仕組み上残る。
/// どちらかの名前を変える時はもう片方も直す。
module BluePrompt.SkillFile

/// スキル本体。Claude CodeとOpenCodeが読む。
let skill: string = "SKILL.md"

/// Open WebUIのModel向けの本文。
/// スキル本体と同じ内容を、
/// 参照ファイルがインライン化され、ナレッジが自動で渡される前提の書き方で持つ。
/// open-webui-modelはこれがあればスキル本体より優先する。
let model: string = "MODEL.md"

/// 生徒に固有の手書きの部分を書くファイル。
/// 本文を生成するための入力で、フロントマターと本文を持つ。
let character: string = "character.md"
