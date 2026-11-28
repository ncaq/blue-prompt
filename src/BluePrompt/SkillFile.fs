/// スキルのディレクトリで名前が決まっているファイル。
/// 同じ名前をModelを組み立てるOpenWebuiと、
/// Knowledgeを組み立てるOpenWebuiKnowledgeと、
/// 本文を生成するRolePlayが別々に必要とするため、ここを唯一の出どころにする。
///
/// 配布物から除く名前の一覧はflake.nixのnonSkillNamesが別に持っている。
/// Nixへ定数を渡す手段が無いので重複は仕組み上残る。
/// どちらかの名前を変える時はもう片方も直す。
module BluePrompt.SkillFile

/// スキル本体。Claude CodeとOpenCodeが読み、Open WebUIのModelのシステムプロンプトにもなる。
let skill: string = "SKILL.md"

/// 生徒に固有の手書きの部分を書くファイル。
/// 本文を生成するための入力で、フロントマターと本文を持つ。
let character: string = "character.md"

/// 代表的な発言を1つ1つのMarkdownとして置くディレクトリ。
/// 本文を生成するための入力で、生徒ごとに0個以上を手で書く。
let quote: string = "quote"

/// 生徒個別スキルのディレクトリに置く、読んだページの型(Character)のJSON。
/// role-playスキルの本文を、wikiruへアクセスせずに型から組み立てるための入力で、
/// Markdownへ書き出した後の文字列を読み直さずに済むようにする。
/// SKILL.mdのcharacter.mdと名前の幹が同じだが、こちらは生徒個別スキルの側に置くので衝突しない。
let characterJson: string = "character.json"

/// スキル本体の骨格になる、全生徒で共通のテンプレート。
let skillTemplate: string = "SKILL.template.md"
