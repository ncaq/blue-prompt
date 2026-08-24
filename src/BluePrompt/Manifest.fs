/// リポジトリの生成物の一覧と、その一括更新。
/// 一覧は外部の設定ファイルではなくここにF#の値として書く。
/// コンパイラが型を検査し、コメントも書け、パーサも要らない。
/// dotnet runは元々コンパイルするので、足す手間も変わらない。
/// 新しい生徒は個別コマンドで生成してから、以後の一括更新に含めるためここへ足す。
module BluePrompt.Manifest

open System.IO
open System.Threading
open System.Threading.Tasks

/// 一括更新で失敗した対象の表示名と原因。
/// 1件の失敗で他の対象を巻き添えにせず、全ての失敗をまとめて報告する。
exception GenerationFailed of failures: (string * exn) list

/// キャラ呼称表の生成物。role-playスキルの生成も読むため、パスを共有する。
let private appellationJson =
    "plugins/jp-wikiru-bluearchive/skills/character-appellation/appellation.json"

/// 生徒1人の衣装1つ分のwikiruのページと、生成物の置き場所を決める名前。
/// 生徒個別のナレッジのスキルと衣装別の参照ファイルは全てこの1行から導出するので、
/// 対応の食い違いが起きない。
type Student =
    {
        /// wikiruのページ名。素の衣装は生徒名そのままで、衣装違いは「生徒名（衣装名）」。
        Page: string
        /// ベースの生徒のローマ字のスラッグ。
        /// role-playスキルのディレクトリ名と、ナレッジのスキル名の元になる。
        Base: string
        /// 衣装を英語へ訳したスラッグ。素の衣装ならNone。
        Costume: string option
    }

/// 生徒個別のナレッジのスキル名。
/// 素はcharacter-<base>、衣装違いはcharacter-<base>-<costume>。
let studentSkillName (student: Student) : string =
    match student.Costume with
    | None -> $"character-%s{student.Base}"
    | Some costume -> $"character-%s{student.Base}-%s{costume}"

/// role-playスキルの衣装別の参照ファイルの名前。素の衣装はnormal.md。
let referenceFileName (student: Student) : string =
    (student.Costume |> Option.defaultValue "normal") + ".md"

/// 実装済みの全生徒。衣装違いもwikiruのページが分かれているため1行ずつ持つ。
/// 並びはベースの生徒の五十音順で、素の衣装を先頭に衣装違いが続く。
/// 出典はwikiruのキャラクター一覧で、NPCは含めない。
let students: Student list =
    [ { Page = "アイリ"
        Base = "airi"
        Costume = None }
      { Page = "アイリ（バンド）"
        Base = "airi"
        Costume = Some "band" }
      { Page = "アオバ"
        Base = "aoba"
        Costume = None }
      { Page = "アカネ"
        Base = "akane"
        Costume = None }
      { Page = "アカネ（バニーガール）"
        Base = "akane"
        Costume = Some "bunny-girl" }
      { Page = "アカネ（制服）"
        Base = "akane"
        Costume = Some "uniform" }
      { Page = "アカリ"
        Base = "akari"
        Costume = None }
      { Page = "アカリ（正月）"
        Base = "akari"
        Costume = Some "new-year" }
      { Page = "アコ"
        Base = "ako"
        Costume = None }
      { Page = "アコ（ドレス）"
        Base = "ako"
        Costume = Some "dress" }
      { Page = "アスナ"
        Base = "asuna"
        Costume = None }
      { Page = "アスナ（バニーガール）"
        Base = "asuna"
        Costume = Some "bunny-girl" }
      { Page = "アスナ（制服）"
        Base = "asuna"
        Costume = Some "uniform" }
      { Page = "アズサ"
        Base = "azusa"
        Costume = None }
      { Page = "アズサ（水着）"
        Base = "azusa"
        Costume = Some "swimsuit" }
      { Page = "アツコ"
        Base = "atsuko"
        Costume = None }
      { Page = "アツコ（水着）"
        Base = "atsuko"
        Costume = Some "swimsuit" }
      { Page = "アヤネ"
        Base = "ayane"
        Costume = None }
      { Page = "アヤネ（水着）"
        Base = "ayane"
        Costume = Some "swimsuit" }
      { Page = "アリス"
        Base = "aris"
        Costume = None }
      { Page = "アリス（メイド）"
        Base = "aris"
        Costume = Some "maid" }
      { Page = "アリス（臨戦）"
        Base = "aris"
        Costume = Some "battle" }
      { Page = "アル"
        Base = "aru"
        Costume = None }
      { Page = "アル（ドレス）"
        Base = "aru"
        Costume = Some "dress" }
      { Page = "アル（正月）"
        Base = "aru"
        Costume = Some "new-year" }
      { Page = "イオリ"
        Base = "iori"
        Costume = None }
      { Page = "イオリ（水着）"
        Base = "iori"
        Costume = Some "swimsuit" }
      { Page = "イズナ"
        Base = "izuna"
        Costume = None }
      { Page = "イズナ（水着）"
        Base = "izuna"
        Costume = Some "swimsuit" }
      { Page = "イズミ"
        Base = "izumi"
        Costume = None }
      { Page = "イズミ（正月）"
        Base = "izumi"
        Costume = Some "new-year" }
      { Page = "イズミ（水着）"
        Base = "izumi"
        Costume = Some "swimsuit" }
      { Page = "イチカ"
        Base = "ichika"
        Costume = None }
      { Page = "イチカ（水着）"
        Base = "ichika"
        Costume = Some "swimsuit" }
      { Page = "イブキ"
        Base = "ibuki"
        Costume = None }
      { Page = "イブキ（水着）"
        Base = "ibuki"
        Costume = Some "swimsuit" }
      { Page = "イロハ"
        Base = "iroha"
        Costume = None }
      { Page = "イロハ（水着）"
        Base = "iroha"
        Costume = Some "swimsuit" }
      { Page = "ウイ"
        Base = "ui"
        Costume = None }
      { Page = "ウイ（水着）"
        Base = "ui"
        Costume = Some "swimsuit" }
      { Page = "ウタハ"
        Base = "utaha"
        Costume = None }
      { Page = "ウタハ（応援団）"
        Base = "utaha"
        Costume = Some "cheer-squad" }
      { Page = "ウミカ"
        Base = "umika"
        Costume = None }
      { Page = "エイミ"
        Base = "eimi"
        Costume = None }
      { Page = "エイミ（水着）"
        Base = "eimi"
        Costume = Some "swimsuit" }
      { Page = "エイミ（臨戦）"
        Base = "eimi"
        Costume = Some "battle" }
      { Page = "エリ"
        Base = "eri"
        Costume = None }
      { Page = "エリカ"
        Base = "erika"
        Costume = None }
      { Page = "オトギ"
        Base = "otogi"
        Costume = None }
      { Page = "カエデ"
        Base = "kaede"
        Costume = None }
      { Page = "カスミ"
        Base = "kasumi"
        Costume = None }
      { Page = "カズサ"
        Base = "kazusa"
        Costume = None }
      { Page = "カズサ（バンド）"
        Base = "kazusa"
        Costume = Some "band" }
      { Page = "カノエ"
        Base = "kanoe"
        Costume = None }
      { Page = "カホ"
        Base = "kaho"
        Costume = None }
      { Page = "カヨコ"
        Base = "kayoko"
        Costume = None }
      { Page = "カヨコ（ドレス）"
        Base = "kayoko"
        Costume = Some "dress" }
      { Page = "カヨコ（正月）"
        Base = "kayoko"
        Costume = Some "new-year" }
      { Page = "カリン"
        Base = "karin"
        Costume = None }
      { Page = "カリン（バニーガール）"
        Base = "karin"
        Costume = Some "bunny-girl" }
      { Page = "カリン（制服）"
        Base = "karin"
        Costume = Some "uniform" }
      { Page = "カンナ"
        Base = "kanna"
        Costume = None }
      { Page = "カンナ（水着）"
        Base = "kanna"
        Costume = Some "swimsuit" }
      { Page = "キキョウ"
        Base = "kikyou"
        Costume = None }
      { Page = "キキョウ（水着）"
        Base = "kikyou"
        Costume = Some "swimsuit" }
      { Page = "キサキ"
        Base = "kisaki"
        Costume = None }
      { Page = "キサキ（水着）"
        Base = "kisaki"
        Costume = Some "swimsuit" }
      { Page = "キララ"
        Base = "kirara"
        Costume = None }
      { Page = "キリノ"
        Base = "kirino"
        Costume = None }
      { Page = "キリノ（水着）"
        Base = "kirino"
        Costume = Some "swimsuit" }
      { Page = "クルミ"
        Base = "kurumi"
        Costume = None }
      { Page = "ケイ"
        Base = "kei"
        Costume = None }
      { Page = "ココナ"
        Base = "kokona"
        Costume = None }
      { Page = "コタマ"
        Base = "kotama"
        Costume = None }
      { Page = "コタマ（キャンプ）"
        Base = "kotama"
        Costume = Some "camp" }
      { Page = "コトリ"
        Base = "kotori"
        Costume = None }
      { Page = "コトリ（応援団）"
        Base = "kotori"
        Costume = Some "cheer-squad" }
      { Page = "コノカ"
        Base = "konoka"
        Costume = None }
      { Page = "コハル"
        Base = "koharu"
        Costume = None }
      { Page = "コハル（水着）"
        Base = "koharu"
        Costume = Some "swimsuit" }
      { Page = "コユキ"
        Base = "koyuki"
        Costume = None }
      { Page = "コユキ（パジャマ）"
        Base = "koyuki"
        Costume = Some "pajama" }
      { Page = "サオリ"
        Base = "saori"
        Costume = None }
      { Page = "サオリ（ドレス）"
        Base = "saori"
        Costume = Some "dress" }
      { Page = "サオリ（水着）"
        Base = "saori"
        Costume = Some "swimsuit" }
      { Page = "サキ"
        Base = "saki"
        Costume = None }
      { Page = "サキ（水着）"
        Base = "saki"
        Costume = Some "swimsuit" }
      { Page = "サクラコ"
        Base = "sakurako"
        Costume = None }
      { Page = "サクラコ（アイドル）"
        Base = "sakurako"
        Costume = Some "idol" }
      { Page = "サツキ"
        Base = "satsuki"
        Costume = None }
      { Page = "サツキ（水着）"
        Base = "satsuki"
        Costume = Some "swimsuit" }
      { Page = "サヤ"
        Base = "saya"
        Costume = None }
      { Page = "サヤ（私服）"
        Base = "saya"
        Costume = Some "casual" }
      { Page = "シグレ"
        Base = "shigure"
        Costume = None }
      { Page = "シグレ（温泉）"
        Base = "shigure"
        Costume = Some "hot-spring" }
      { Page = "シズコ"
        Base = "shizuko"
        Costume = None }
      { Page = "シズコ（水着）"
        Base = "shizuko"
        Costume = Some "swimsuit" }
      { Page = "シミコ"
        Base = "shimiko"
        Costume = None }
      { Page = "シュン"
        Base = "shun"
        Costume = None }
      { Page = "シュン（幼女）"
        Base = "shun"
        Costume = Some "small" }
      { Page = "シュン（水着）"
        Base = "shun"
        Costume = Some "swimsuit" }
      { Page = "シロコ"
        Base = "shiroko"
        Costume = None }
      { Page = "シロコ（ライディング）"
        Base = "shiroko"
        Costume = Some "riding" }
      { Page = "シロコ（水着）"
        Base = "shiroko"
        Costume = Some "swimsuit" }
      { Page = "シロコ＊テラー"
        Base = "shiroko-terror"
        Costume = None }
      { Page = "ジュリ"
        Base = "juri"
        Costume = None }
      { Page = "ジュリ（アルバイト）"
        Base = "juri"
        Costume = Some "part-timer" }
      { Page = "ジュンコ"
        Base = "junko"
        Costume = None }
      { Page = "ジュンコ（正月）"
        Base = "junko"
        Costume = Some "new-year" }
      { Page = "スズミ"
        Base = "suzumi"
        Costume = None }
      { Page = "スズミ（マジカル）"
        Base = "suzumi"
        Costume = Some "magical" }
      { Page = "スバル"
        Base = "subaru"
        Costume = None }
      { Page = "スミレ"
        Base = "sumire"
        Costume = None }
      { Page = "スミレ（アルバイト）"
        Base = "sumire"
        Costume = Some "part-timer" }
      { Page = "セイア"
        Base = "seia"
        Costume = None }
      { Page = "セイア（水着）"
        Base = "seia"
        Costume = Some "swimsuit" }
      { Page = "セナ"
        Base = "sena"
        Costume = None }
      { Page = "セナ（私服）"
        Base = "sena"
        Costume = Some "casual" }
      { Page = "セリカ"
        Base = "serika"
        Costume = None }
      { Page = "セリカ（正月）"
        Base = "serika"
        Costume = Some "new-year" }
      { Page = "セリカ（水着）"
        Base = "serika"
        Costume = Some "swimsuit" }
      { Page = "セリナ"
        Base = "serina"
        Costume = None }
      { Page = "セリナ（クリスマス）"
        Base = "serina"
        Costume = Some "christmas" }
      { Page = "タカネ"
        Base = "takane"
        Costume = None }
      { Page = "チアキ"
        Base = "chiaki"
        Costume = None }
      { Page = "チアキ（水着）"
        Base = "chiaki"
        Costume = Some "swimsuit" }
      { Page = "チェリノ"
        Base = "cherino"
        Costume = None }
      { Page = "チェリノ（温泉）"
        Base = "cherino"
        Costume = Some "hot-spring" }
      { Page = "チセ"
        Base = "chise"
        Costume = None }
      { Page = "チセ（水着）"
        Base = "chise"
        Costume = Some "swimsuit" }
      { Page = "チナツ"
        Base = "chinatsu"
        Costume = None }
      { Page = "チナツ（温泉）"
        Base = "chinatsu"
        Costume = Some "hot-spring" }
      { Page = "チヒロ"
        Base = "chihiro"
        Costume = None }
      { Page = "ツクヨ"
        Base = "tsukuyo"
        Costume = None }
      { Page = "ツクヨ（ドレス）"
        Base = "tsukuyo"
        Costume = Some "dress" }
      { Page = "ツバキ"
        Base = "tsubaki"
        Costume = None }
      { Page = "ツバキ（ガイド）"
        Base = "tsubaki"
        Costume = Some "guide" }
      { Page = "ツルギ"
        Base = "tsurugi"
        Costume = None }
      { Page = "ツルギ（水着）"
        Base = "tsurugi"
        Costume = Some "swimsuit" }
      { Page = "トキ"
        Base = "toki"
        Costume = None }
      { Page = "トキ（バニーガール）"
        Base = "toki"
        Costume = Some "bunny-girl" }
      { Page = "トキ（臨戦）"
        Base = "toki"
        Costume = Some "battle" }
      { Page = "トモエ"
        Base = "tomoe"
        Costume = None }
      { Page = "トモエ（チーパオ）"
        Base = "tomoe"
        Costume = Some "qipao" }
      { Page = "ナギサ"
        Base = "nagisa"
        Costume = None }
      { Page = "ナギサ（水着）"
        Base = "nagisa"
        Costume = Some "swimsuit" }
      { Page = "ナグサ"
        Base = "nagusa"
        Costume = None }
      { Page = "ナグサ（水着）"
        Base = "nagusa"
        Costume = Some "swimsuit" }
      { Page = "ナツ"
        Base = "natsu"
        Costume = None }
      { Page = "ナツ（バンド）"
        Base = "natsu"
        Costume = Some "band" }
      { Page = "ニコ"
        Base = "niko"
        Costume = None }
      { Page = "ニヤ"
        Base = "niya"
        Costume = None }
      { Page = "ネル"
        Base = "neru"
        Costume = None }
      { Page = "ネル（バニーガール）"
        Base = "neru"
        Costume = Some "bunny-girl" }
      { Page = "ネル（制服）"
        Base = "neru"
        Costume = Some "uniform" }
      { Page = "ノア"
        Base = "noa"
        Costume = None }
      { Page = "ノア（パジャマ）"
        Base = "noa"
        Costume = Some "pajama" }
      { Page = "ノゾミ"
        Base = "nozomi"
        Costume = None }
      { Page = "ノドカ"
        Base = "nodoka"
        Costume = None }
      { Page = "ノドカ（温泉）"
        Base = "nodoka"
        Costume = Some "hot-spring" }
      { Page = "ノノミ"
        Base = "nonomi"
        Costume = None }
      { Page = "ノノミ（水着）"
        Base = "nonomi"
        Costume = Some "swimsuit" }
      { Page = "ハスミ"
        Base = "hasumi"
        Costume = None }
      { Page = "ハスミ（体操服）"
        Base = "hasumi"
        Costume = Some "track" }
      { Page = "ハスミ（水着）"
        Base = "hasumi"
        Costume = Some "swimsuit" }
      { Page = "ハナエ"
        Base = "hanae"
        Costume = None }
      { Page = "ハナエ（クリスマス）"
        Base = "hanae"
        Costume = Some "christmas" }
      { Page = "ハナコ"
        Base = "hanako"
        Costume = None }
      { Page = "ハナコ（水着）"
        Base = "hanako"
        Costume = Some "swimsuit" }
      { Page = "ハルカ"
        Base = "haruka"
        Costume = None }
      { Page = "ハルカ（ドレス）"
        Base = "haruka"
        Costume = Some "dress" }
      { Page = "ハルカ（正月）"
        Base = "haruka"
        Costume = Some "new-year" }
      { Page = "ハルナ"
        Base = "haruna"
        Costume = None }
      { Page = "ハルナ（体操服）"
        Base = "haruna"
        Costume = Some "track" }
      { Page = "ハルナ（正月）"
        Base = "haruna"
        Costume = Some "new-year" }
      { Page = "ハレ"
        Base = "hare"
        Costume = None }
      { Page = "ハレ（キャンプ）"
        Base = "hare"
        Costume = Some "camp" }
      { Page = "ヒカリ"
        Base = "hikari"
        Costume = None }
      { Page = "ヒナ"
        Base = "hina"
        Costume = None }
      { Page = "ヒナ（ドレス）"
        Base = "hina"
        Costume = Some "dress" }
      { Page = "ヒナ（水着）"
        Base = "hina"
        Costume = Some "swimsuit" }
      { Page = "ヒナタ"
        Base = "hinata"
        Costume = None }
      { Page = "ヒナタ（水着）"
        Base = "hinata"
        Costume = Some "swimsuit" }
      { Page = "ヒビキ"
        Base = "hibiki"
        Costume = None }
      { Page = "ヒビキ（応援団）"
        Base = "hibiki"
        Costume = Some "cheer-squad" }
      { Page = "ヒフミ"
        Base = "hifumi"
        Costume = None }
      { Page = "ヒフミ（水着）"
        Base = "hifumi"
        Costume = Some "swimsuit" }
      { Page = "ヒマリ"
        Base = "himari"
        Costume = None }
      { Page = "ヒマリ（臨戦）"
        Base = "himari"
        Costume = Some "battle" }
      { Page = "ヒヨリ"
        Base = "hiyori"
        Costume = None }
      { Page = "ヒヨリ（水着）"
        Base = "hiyori"
        Costume = Some "swimsuit" }
      { Page = "フィーナ"
        Base = "fina"
        Costume = None }
      { Page = "フィーナ（ガイド）"
        Base = "fina"
        Costume = Some "guide" }
      { Page = "フウカ"
        Base = "fuuka"
        Costume = None }
      { Page = "フウカ（正月）"
        Base = "fuuka"
        Costume = Some "new-year" }
      { Page = "フブキ"
        Base = "fubuki"
        Costume = None }
      { Page = "フブキ（水着）"
        Base = "fubuki"
        Costume = Some "swimsuit" }
      { Page = "フユ"
        Base = "fuyu"
        Costume = None }
      { Page = "ホシノ"
        Base = "hoshino"
        Costume = None }
      { Page = "ホシノ（水着）"
        Base = "hoshino"
        Costume = Some "swimsuit" }
      { Page = "ホシノ（臨戦）"
        Base = "hoshino"
        Costume = Some "battle" }
      { Page = "マキ"
        Base = "maki"
        Costume = None }
      { Page = "マキ（キャンプ）"
        Base = "maki"
        Costume = Some "camp" }
      { Page = "マコト"
        Base = "makoto"
        Costume = None }
      { Page = "マコト（水着）"
        Base = "makoto"
        Costume = Some "swimsuit" }
      { Page = "マシロ"
        Base = "mashiro"
        Costume = None }
      { Page = "マシロ（水着）"
        Base = "mashiro"
        Costume = Some "swimsuit" }
      { Page = "マリー"
        Base = "mari"
        Costume = None }
      { Page = "マリー（アイドル）"
        Base = "mari"
        Costume = Some "idol" }
      { Page = "マリー（体操服）"
        Base = "mari"
        Costume = Some "track" }
      { Page = "マリナ"
        Base = "marina"
        Costume = None }
      { Page = "マリナ（チーパオ）"
        Base = "marina"
        Costume = Some "qipao" }
      { Page = "ミカ"
        Base = "mika"
        Costume = None }
      { Page = "ミカ（水着）"
        Base = "mika"
        Costume = Some "swimsuit" }
      { Page = "ミサキ"
        Base = "misaki"
        Costume = None }
      { Page = "ミサキ（水着）"
        Base = "misaki"
        Costume = Some "swimsuit" }
      { Page = "ミチル"
        Base = "michiru"
        Costume = None }
      { Page = "ミチル（ドレス）"
        Base = "michiru"
        Costume = Some "dress" }
      { Page = "ミドリ"
        Base = "midori"
        Costume = None }
      { Page = "ミドリ（メイド）"
        Base = "midori"
        Costume = Some "maid" }
      { Page = "ミナ"
        Base = "mina"
        Costume = None }
      { Page = "ミネ"
        Base = "mine"
        Costume = None }
      { Page = "ミネ（アイドル）"
        Base = "mine"
        Costume = Some "idol" }
      { Page = "ミノリ"
        Base = "minori"
        Costume = None }
      { Page = "ミモリ"
        Base = "mimori"
        Costume = None }
      { Page = "ミモリ（水着）"
        Base = "mimori"
        Costume = Some "swimsuit" }
      { Page = "ミヤコ"
        Base = "miyako"
        Costume = None }
      { Page = "ミヤコ（水着）"
        Base = "miyako"
        Costume = Some "swimsuit" }
      { Page = "ミユ"
        Base = "miyu"
        Costume = None }
      { Page = "ミユ（水着）"
        Base = "miyu"
        Costume = Some "swimsuit" }
      { Page = "ミヨ"
        Base = "miyo"
        Costume = None }
      { Page = "ムツキ"
        Base = "mutsuki"
        Costume = None }
      { Page = "ムツキ（ドレス）"
        Base = "mutsuki"
        Costume = Some "dress" }
      { Page = "ムツキ（正月）"
        Base = "mutsuki"
        Costume = Some "new-year" }
      { Page = "メグ"
        Base = "megu"
        Costume = None }
      { Page = "メル"
        Base = "meru"
        Costume = None }
      { Page = "モエ"
        Base = "moe"
        Costume = None }
      { Page = "モエ（水着）"
        Base = "moe"
        Costume = Some "swimsuit" }
      { Page = "モミジ"
        Base = "momiji"
        Costume = None }
      { Page = "モモイ"
        Base = "momoi"
        Costume = None }
      { Page = "モモイ（メイド）"
        Base = "momoi"
        Costume = Some "maid" }
      { Page = "ヤクモ"
        Base = "yakumo"
        Costume = None }
      { Page = "ユウカ"
        Base = "yuuka"
        Costume = None }
      { Page = "ユウカ（パジャマ）"
        Base = "yuuka"
        Costume = Some "pajama" }
      { Page = "ユウカ（体操服）"
        Base = "yuuka"
        Costume = Some "track" }
      { Page = "ユカリ"
        Base = "yukari"
        Costume = None }
      { Page = "ユカリ（水着）"
        Base = "yukari"
        Costume = Some "swimsuit" }
      { Page = "ユズ"
        Base = "yuzu"
        Costume = None }
      { Page = "ユズ（メイド）"
        Base = "yuzu"
        Costume = Some "maid" }
      { Page = "ユズ（臨戦）"
        Base = "yuzu"
        Costume = Some "battle" }
      { Page = "ヨシミ"
        Base = "yoshimi"
        Costume = None }
      { Page = "ヨシミ（バンド）"
        Base = "yoshimi"
        Costume = Some "band" }
      { Page = "ラブ"
        Base = "love"
        Costume = None }
      { Page = "リオ"
        Base = "rio"
        Costume = None }
      { Page = "リオ（臨戦）"
        Base = "rio"
        Costume = Some "battle" }
      { Page = "リツ"
        Base = "ritsu"
        Costume = None }
      { Page = "ルミ"
        Base = "rumi"
        Costume = None }
      { Page = "レイ"
        Base = "rei"
        Costume = None }
      { Page = "レイサ"
        Base = "reisa"
        Costume = None }
      { Page = "レイサ（マジカル）"
        Base = "reisa"
        Costume = Some "magical" }
      { Page = "レイジョ"
        Base = "reijo"
        Costume = None }
      { Page = "レナ"
        Base = "rena"
        Costume = None }
      { Page = "レンゲ"
        Base = "renge"
        Costume = None }
      { Page = "レンゲ（水着）"
        Base = "renge"
        Costume = Some "swimsuit" }
      { Page = "ワカモ"
        Base = "wakamo"
        Costume = None }
      { Page = "ワカモ（水着）"
        Base = "wakamo"
        Costume = Some "swimsuit" }
      { Page = "御坂美琴"
        Base = "misaka-mikoto"
        Costume = None }
      { Page = "佐天涙子"
        Base = "saten-ruiko"
        Costume = None }
      { Page = "初音ミク"
        Base = "hatsune-miku"
        Costume = None }
      { Page = "食蜂操祈"
        Base = "shokuhou-misaki"
        Costume = None } ]

/// wikiruから取り込む対象。パスはリポジトリのルートからの相対。
let wikiruTargets: Target.WikiruTarget list =
    [ Target.Appellation(
          "キャラ呼称表",
          "plugins/jp-wikiru-bluearchive/skills/character-appellation/reference.md",
          appellationJson
      )
      Target.School(
          "学校別",
          "plugins/jp-wikiru-bluearchive/skills/character-index-by-group/reference.md"
      ) ]
    @ (students
       |> List.collect (fun student ->
           [ Target.StudentSkill(
                 student.Page,
                 $"plugins/jp-wikiru-bluearchive/skills/%s{studentSkillName student}/SKILL.md"
             )
             Target.RolePlayReference(
                 student.Page,
                 $"plugins/role-play/skills/%s{student.Base}/%s{referenceFileName student}"
             ) ]))

/// テンプレートから本文を生成するrole-playスキル。パスはリポジトリのルートからの相対。
/// 素の衣装の1行からベースの生徒ごとに1つ導出する。
/// 衣装違いは同じディレクトリの参照ファイルとして生成時に読まれるため、ここには現れない。
let rolePlaySkills: Target.RolePlaySkill list =
    students
    |> List.filter (fun student -> Option.isNone student.Costume)
    |> List.map (fun student ->
        { Caller = student.Page
          Template = "plugins/role-play"
          Appellation = appellationJson
          Output = $"plugins/role-play/skills/%s{student.Base}" })

/// 同時に進める対象の数。
/// wikiru側の負荷は1台のPCから送る量では誤差だが、
/// 無駄に並べても利益が無く、1対象ごとにpandocのプロセスも立つため、常識的な数に収める。
let degreeOfParallelism: int = 16

/// 名前付きの処理を同時実行数を絞って並列に走らせ、結果を名前ごとのResultで返す。
/// 1件の失敗で他を打ち切らず全件を走らせ切り、
/// 失敗をまとめて報告できるようにParallel.ForEachAsyncではなくこの形にする。
/// 結果の並びは入力の並びと同じ。
let runBounded
    (degree: int)
    (works: (string * (unit -> Task<'T>)) list)
    : Task<(string * Result<'T, exn>) list> =
    task {
        use gate = new SemaphoreSlim(degree)

        let! results =
            works
            |> List.map (fun (name, work) ->
                task {
                    do! gate.WaitAsync()

                    try
                        try
                            let! result = work ()
                            return name, Ok result
                        with error ->
                            return name, Error error
                    finally
                        gate.Release() |> ignore
                })
            |> Task.WhenAll

        return List.ofArray results
    }

/// 成功した結果のパスを集め、失敗があればそれらを束ねて返す。
let private partition
    (results: (string * Result<string list, exn>) list)
    : string list * (string * exn) list =
    let paths =
        results
        |> List.collect (fun (_, result) ->
            match result with
            | Ok paths -> paths
            | Error _ -> [])

    let failures =
        results
        |> List.choose (fun (name, result) ->
            match result with
            | Ok _ -> None
            | Error error -> Some(name, error))

    paths, failures

/// character.mdが挙げるナレッジのうち実在しなかった名前を、character.mdのパスごとに並べたもの。
exception KnowledgeSkillMissing of failures: (string * string list) list

/// 生徒個別スキルの名前。スキル名は出力先のディレクトリの名前と一致する。
let studentSkillNames (targets: Target.WikiruTarget list) : Set<string> =
    targets
    |> List.choose (function
        | Target.StudentSkill(_, output) -> Path.GetDirectoryName output |> Path.GetFileName |> Some
        | _ -> None)
    |> Set.ofList

/// character.mdが挙げるナレッジのうち、生徒個別スキルとして実在しない名前を集める。
/// この名前は生成物へ参照先としてそのまま書き出されるため、
/// 綴りを間違えても生成対象を足し忘れても、生成物の差分だけでは気付けない。
/// 実在するものだけになっていれば空を返す。
let missingKnowledgeSkills
    (known: Set<string>)
    (declared: (string * string list) list)
    : (string * string list) list =
    declared
    |> List.choose (fun (path, knowledge) ->
        match
            RolePlay.studentKnowledgeNames knowledge
            |> List.filter (fun name -> not (Set.contains name known))
        with
        | [] -> None
        | missing -> Some(path, missing))

/// role-playスキルのcharacter.mdが挙げるナレッジが、全て実在する生徒個別スキルかを確かめる。
/// 実在しない名前があればKnowledgeSkillMissingを送出する。
/// wikiruTargetsとrolePlaySkillsの両方を知っているのはここだけなので、この検査もここが持つ。
///
/// character.mdが無い場合やフロントマターが壊れている場合は、
/// 書き出しの中で起きた時と同じく対象ごとの理由として報告したいので、
/// 1件の失敗で他の対象の読み込みを打ち切らずGenerationFailedへ束ねる。
///
/// ただし1件でも読めなければ書き出しは1つも行わない。
/// 書き出しの失敗は対象ごとに独立しているが、
/// 手書きの部分を読めない状態はマニフェストか作業の途中を疑うべき状況で、
/// 半分だけ更新された生成物を残すより、全て止めて直してもらうほうが分かりやすいため。
let private checkKnowledgeSkills (root: string) : Task<unit> =
    task {
        let declared = ResizeArray()
        // 読み取れなかった失敗と、読み取れた上で実在しなかった名前は、
        // 束ねる型も送出する例外も違うので名前で区別する。
        let readFailures = ResizeArray()

        for skill in rolePlaySkills do
            let path = Path.Combine(root, skill.Output, SkillFile.character)

            try
                let! content = File.ReadAllTextAsync path
                declared.Add(path, (OpenWebui.parseFrontmatter path content).Knowledge)
            with error ->
                readFailures.Add(Target.rolePlayName skill, error)

        if 0 < readFailures.Count then
            return raise (GenerationFailed(List.ofSeq readFailures))

        match missingKnowledgeSkills (studentSkillNames wikiruTargets) (List.ofSeq declared) with
        | [] -> ()
        | failures -> return raise (KnowledgeSkillMissing failures)
    }

/// role-playスキルを全て並列に書き出し、書いたパスを返す。整形は掛けない。
/// 失敗があればGenerationFailedを送出する。
let writeRolePlaySkills (root: string) : Task<string list> =
    task {
        // ナレッジの検査が効くのはこの一括更新の経路だけで、roleplay skillの単体の生成は素通りする。
        // 掛ける場所を増やしても、Manifestへ足す前のスキルはrolePlaySkillsに載っておらず検査の対象にならない。
        // どちらにせよManifestへ足した後の一括更新で落ちるので、ここ1箇所に留める。
        do! checkKnowledgeSkills root

        let! results =
            rolePlaySkills
            |> List.map (fun skill ->
                Target.rolePlayName skill,
                (fun () -> Target.writeRolePlay (Target.resolveRolePlay root skill)))
            |> runBounded degreeOfParallelism

        match partition results with
        | paths, [] -> return paths
        | _, failures -> return raise (GenerationFailed failures)
    }

/// role-playスキルを全て生成し直してから、まとめてnix fmtを掛ける。
/// wikiruへはアクセスしないため、テンプレートの変更の反映と生成物の検査に使う。
let createRolePlaySkills (root: string) : Task<unit> =
    task {
        let! paths = writeRolePlaySkills root
        do! Fmt.formatFiles paths
    }

/// wikiruの取得の結果を受けて、role-playスキルの生成と整形と失敗の報告を決める。
/// 全て成功していればrole-playスキルを書き出し、
/// wikiruとrole-playのパスをまとめてformatFilesの1回で整形する。
/// 失敗があれば成功した分だけを整形して途中まで更新された状態を整えた上で、
/// GenerationFailedを送出する。
/// その整形が落ちた場合も取得の失敗の理由は失わず、整形の失敗を一覧へ足して送出する。
/// role-playスキルは呼称表と衣装別の参照ファイルを読むため、
/// それらが古いままかもしれない失敗時には生成し直さない。
/// 整形とrole-playの書き出しは引数で受け取り、
/// wikiruへ取りに行かずにこの分岐を検証できるようにする。
let finish
    (formatFiles: string list -> Task<unit>)
    (writeRolePlay: unit -> Task<string list>)
    (results: (string * Result<string list, exn>) list)
    : Task<unit> =
    task {
        match partition results with
        | wikiruPaths, [] ->
            let! rolePlayPaths = writeRolePlay ()
            do! formatFiles (wikiruPaths @ rolePlayPaths)
        | wikiruPaths, failures ->
            // 整形が落ちても取得の失敗の理由は報告したいので、
            // 整形の失敗は失敗の一覧の末尾へ加えてまとめて送出する。
            let! formatFailure =
                task {
                    try
                        do! formatFiles wikiruPaths
                        return []
                    with error ->
                        return [ "nix fmt", error ]
                }

            return raise (GenerationFailed(failures @ formatFailure))
    }

/// wikiruの対象を全て並列に取得して書き出し、
/// 続けてrole-playスキルを生成し直してから、まとめてnix fmtを1回だけ掛ける。
/// 失敗時の扱いはfinishのとおり。
let createAll (root: string) : Task<unit> =
    task {
        // ナレッジの検査はwikiruへアクセスせず手元のファイルだけで結果が決まるので、
        // 取得より先に掛ける。
        // 後段のwriteRolePlaySkillsでも走るが、そこまで進むと綴りの間違い1つで全ページの取得を待たされる上に、
        // finishの成功側は書き出しが落ちると整形へ到達せず、未整形の生成物がディスクへ残る。
        do! checkKnowledgeSkills root

        let! results =
            wikiruTargets
            |> List.map (fun target ->
                Target.wikiruName target,
                (fun () -> Target.writeWikiru (Target.resolveWikiru root target)))
            |> runBounded degreeOfParallelism

        do! finish Fmt.formatFiles (fun () -> writeRolePlaySkills root) results
    }
