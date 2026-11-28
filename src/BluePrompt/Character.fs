/// 生徒個別ページの型。
/// 汎用の記事の木は「見出しと項目の並び」としてしか中身を知らないが、
/// 生徒個別ページは節ごとに決まった形を持っているので、その形に名前を付ける。
/// 値は並びとブロックのまま持ち、数値へは読まない。
/// ステータスの「3146/47611(★2Lv90)/…」のような値は表記の揺れが多く、読み解いても書き戻すだけだからである。
/// 専用の型で読めなかった節は汎用の木のまま持つので、中身を黙って落とすことは無い。
module BluePrompt.Character

open BluePrompt.Article

/// キーと値の1項目。「名前: ユウカ」のような組。値が空欄の項目は値が空の並びになる。
type Entry =
    { Key: Inline list; Value: Inline list }

/// 列名を添えた値の並びの1行。「Lv: 1, コスト: 3, 効果: …」のような行。
/// 行の見出しを持つ表の行は「攻撃力: Lv.1: 43, Lv.30: 156」のようにキーを持つ。
type Row =
    { Key: Inline list option
      Fields: (string * Inline list) list }

/// 題名を持つ行の並び。スキル名を題名にしたLvごとの効果の表など。
type Chart = { Title: string; Rows: Row list }

/// 基本情報の節。プロフィールの表と、その表を小見出しで区切った残りの部分。
type BasicInformation =
    {
        /// 立ち絵。プロフィールの表が画像の列として持つ。
        Portrait: Inline list option
        /// 名前、フルネーム、学園、…、各バージョン一覧。
        Profile: Entry list
        /// 小見出し「基本情報」の紹介文。
        Introduction: Block list
        /// 小見出し「登場シナリオ」。プレイアブルではない登場人物のページが持つ。
        Appearances: Block list
        /// 小見出し「入手方法」。
        Acquisition: Block list
        /// 小見出し「ステータス(初期値/MAX値)」。
        Status: Entry list
        /// 小見出し「装備（装着可能Lv）」。
        Equipment: Entry list
        /// 最後の表の後ろに続く補足。物語の進行で変わる項目の注記など。
        Supplement: Block list
    }

/// スキルの区分。「EXスキル」「ノーマルスキル」のような見出しと、その下のスキルの表。
/// 姿を切り替える生徒のページは、区分の下にさらに小区分を持つ。
type SkillCategory =
    { Title: Inline list
      Skills: Chart list
      Children: SkillCategory list }

/// 固有武器の節。
type Weapon =
    {
        Name: Inline list
        Icon: Block list
        /// 小見出し「詳細」の説明文。
        Description: Block list
        /// 小見出し「限界突破」。★ごとの効果。
        LimitBreak: Entry list
        /// Lvごとのステータスの伸び。
        Growth: Row list
    }

/// 愛用品の節。
type Gear =
    {
        Name: Inline list
        Icon: Block list
        /// 小見出し「詳細」の説明文。
        Description: Block list
        /// 小見出し「基本情報」。段階ごとの効果。
        /// 「T1: 攻撃速度 1000, 会心値 300」のように1つの段階が複数の効果を持つので、行として持つ。
        Effects: Row list
        /// 小見出し「強化」。段階ごとに要る絆ランクと素材。
        Upgrades: Row list
    }

/// 能力解放の節。
type Potential =
    {
        /// Lvごとのステータスの伸び。
        Growth: Row list
        /// 小見出し「必要素材」。Lvの範囲ごとの素材。
        Materials: Row list
        /// 後ろに続く注記。
        Notes: Block list
    }

/// 絆ランクボーナスの節。
type BondBonus =
    {
        /// ランクの範囲ごとのボーナス。
        Ranks: Row list
        /// 小見出し「ボーナス合計」。別バージョンの生徒も含めた合計を、絆ランクごとの表で持つ。
        Totals: Chart list
        /// 後ろに続く注記。
        Notes: Block list
    }

/// 絆ストーリーの節。
type BondStory =
    {
        /// エピソードの一覧。
        Episodes: Row list
        /// メモリアルロビー以降。BGMと画像とスタッフの並びがページごとに違うので、ブロックのまま持つ。
        Lobby: Block list
    }

/// ボイスの区分。「一般」「戦闘」「ロビー」のような小見出しと、場面ごとのセリフ。
/// 区分を持たない平らな並びのページでは題名が無い。
type VoiceGroup =
    { Title: Inline list option
      Lines: Entry list }

/// 節の中身。専用の型で読めた節と、汎用の木のままの節。
type Content =
    | Basic of BasicInformation
    | Skills of SkillCategory list
    | UniqueWeapon of Weapon
    | FavoriteItem of Gear
    | Potential of Potential
    | BondBonus of BondBonus
    | BondStory of BondStory
    | Voice of VoiceGroup list
    /// 小ネタ。自由記述なのでブロックのまま持つ。
    | Trivia of Block list
    /// 汎用の木のままの節。
    /// 理由を持つのは、専用の型で読もうとして読めなかった節である。
    /// タブで中身を切り替える節と、専用の型を用意していない節は、理由を持たない。
    | Other of Block list * reason: string option

/// h2で分けた1つの節。
type Section =
    { Title: Inline list; Content: Content }

type Character =
    {
        /// 最初の節より前の中身と、h2ではない見出しの節。普通は空。
        Preamble: Block list
        Sections: Section list
        Footnotes: Footnote list
    }

/// 専用の型で読もうとして読めなかった節の、見出しと理由。
/// 生成の時に報告して、型を緩めるか汎用のままにするかを決める材料にする。
let unreadSections (character: Character) : (string * string) list =
    character.Sections
    |> List.choose (fun section ->
        match section.Content with
        | Other(_, Some reason) -> Some((plainText section.Title).Trim(), reason)
        | _ -> None)
