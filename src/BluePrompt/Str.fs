/// 文字列をパターンマッチで扱うための部品。
module BluePrompt.Str

open System

/// 空文字を、値を持たないケースとしてnullと一緒に受ける。
///
/// `match s with | null | "" -> ...`と直に書くと、
/// IONIDE-005がパターンマッチの展開した空文字との`=`を見て、
/// String.IsNullOrEmptyを使えという指摘を出す。
/// 分岐の形はmatchのままが読みやすいので、
/// 判定だけをString.IsNullOrEmptyでここへ閉じ込める。
let (|NullOrEmpty|NonEmpty|) (value: string) =
    if String.IsNullOrEmpty value then
        NullOrEmpty
    else
        NonEmpty value
