# HANDOFF

> 直前の要約: `.agent/handoff/2026-07-09.md`

---

## セッション 2026-07-10 13:05

### 使用ツール
Claude Code（VSCode 拡張 / Fable 5）

### 現在のタスクと進捗
- [x] GitHub リモート接続（origin 設定 + main / 全タグをプッシュ）
- [x] v0.7.3 クリック切り替え時の約10秒フリーズを解消 → リリース・常駐入れ替え済み
- [x] v0.7.4 切り替え時のガチャつく再描画を解消 → リリース・常駐入れ替え済み（ユーザー確認済み）
- [x] 記事ネタ保存（note-articles: 2026-07-10_felt-report-debug）

### GitHub 接続（リモート設定）

- `origin` → https://github.com/bsfukushi/VSCodeLiveTiles を設定し、main + タグ全部をプッシュ
- この PC の GitHub 認証は jimotokara / bsfukushi の 2 アカウント。リポジトリは bsfukushi 所有
- gh CLI に bsfukushi を追加し `gh auth setup-git` 済み。**アクティブが jimotokara に戻って 403 になることがある**
  → `gh auth switch --user bsfukushi` で復旧

### v0.7.3 クリック切り替え時の約10秒フリーズを解消

- 報告: タイルクリック→画面切り替えで約10秒フリーズすることが頻発
- 原因: `MoveMaximizeAndFocus` が UI スレッド上で同期 Win32 呼び出し
  （ShowWindow / SetWindowPos / AttachThreadInput）を行い、VSCode の UI スレッドが
  詰まっている間ウィジェットが巻き添えブロックされていた
- 修正（`Interop/NativeWindows.cs` のみ）:
  - `ShowWindow` → `ShowWindowAsync`（復元・最大化。相手のキューに積むだけで待たない）
  - `SetWindowPos` に `SWP_ASYNCWINDOWPOS` 追加（キューは順番どおり処理されるので
    復元→移動→最大化の順序は保たれる）
  - `AttachThreadInput` 廃止（クリック直後は自プロセスがフォアグラウンドなので
    素の `SetForegroundWindow` で通る。詰まった相手に付くと自分の入力処理まで止まる危険 API）
- publish して常駐プロセスを v0.7.3 に入れ替え済み

### v0.7.4 切り替え時のガチャつく再描画を解消

- とも: 「フリーズは解消！ただタイル切替だとガチャっとずれながら再描画される。タスクバー切替はきれい」
- 原因: クリックのたびに無条件で 復元→移動→最大化 の 3 段階を実行していた。
  すでにメインモニターで最大化済みでも「縮む→動く→最大化し直す」が見えていた
- 修正（`Interop/NativeWindows.cs` のみ）: `MonitorFromWindow` / `MonitorFromPoint` で現在地を判定し、
  - 対象モニターで最大化済み → `SetForegroundWindow` だけ（タスクバー切替と同じ）
  - 対象モニターに居る（最小化・通常）→ `SW_MAXIMIZE` のみ（移動しない）
  - 別モニターのときだけ従来の 復元→移動→最大化
  - 最小化中は座標が画面外（-32000）なので `GetWindowPlacement` の通常時矩形でモニター判定
- publish して常駐プロセスを v0.7.4 に入れ替え済み

### セッションハイライト

- とも: 「検証は不要です。改善案で進めてください」
  → CC: 計測ログを1日仕込む案も添えていたが、同期→非同期化は計測結果によらず正しい修正
  なので即実装へ。信頼ベースで検証フェーズを丸ごとスキップできた
- とも: 「おぉー、切り換えのストレスがなくなったよ！しいて追加で要望を出すなら、切り替え時の
  画面の再描画が、タスクバーで画面を切り換えたときは、きれいに切り替わるんだけど、タイルから
  切り換えたときは、ガチャっとずれながら再描画されるような感じがします」
  → CC: 「タスクバーだときれい」という**比較つきの体感報告**がそのまま原因特定の決め手に。
  タスクバー切替は前面化だけ／タイルは無条件で復元→移動→最大化、の差分が一目で確定した。
  ユーザーの対照実験が二分探索を丸ごと省略してくれた（記事ネタとして保存済み）
- とも: 「完璧です！きれいにストレスなく切り替わるので気持ちいいです！」
  → CC: v0.7.3 / v0.7.4 とも Interop 1ファイルの修正で完結

### 次のセッションで最初にやること

1. v0.7.3〜0.7.4 の常用観察の結果を聞く:
   - フリーズが再発しないか。「切り替わらない（タスクバー点滅で終わる）」が出たら
     `SetForegroundWindow` の失敗を疑う
   - タイル切替の見え方がタスクバー切替と揃ったか
2. 積み残し（TODO.md「積み残し」: 死んだセッションの WaitingPermission 固定 /
   PostToolUseFailure 未対応）を踏んだら着手

### 注意点・ブロッカー

- **gh のアクティブアカウントが勝手に jimotokara に戻ることがある**。このリポジトリへの
  push が 403 になったら `gh auth switch --user bsfukushi`。note-articles へのプッシュは
  逆に jimotokara が必要（今回は切り替え→プッシュ→bsfukushi に戻した）
- 常駐 exe は `publish/`、開発ビルドは `bin/` で別物。更新は
  「ウィジェット終了 → `dotnet publish` → 再起動」の順
- HANDOFF.md / TODO.md / .agent 配下の今日の変更はまだ未コミット

---

## セッション 2026-07-10 13:25

### 使用ツール
Claude Code（VSCode 拡張 / Fable 5）

### 現在のタスクと進捗
- [x] v0.7.3〜0.7.4 常用観察の確認 → 「使用感、完璧です」で問題なし確定
- [x] 一般配布に向けた非機能要件の検証（全ソース約1,500行＋配布物構成を監査）
- [x] 検証結果を `.spec/TODO.md` に Phase 4〜6 として反映（旧「積み残し」2件は Phase 4 に吸収）
- [x] 記事ネタ保存（note-articles: 2026-07-10_distribution-audit、freshness: evergreen）
- [ ] Phase 4 ①②（WaitingPermission 固定解消＋PostToolUseFailure）: 未着手

### 検証レポート要点（詳細は TODO.md Phase 4〜6）
- **強み（そのまま売りになる）**: ネットワーク通信コードゼロ / NuGet 依存ゼロ /
  events.jsonl 読み取り専用 / DWM サムネイル・セッション辞書とも対管理でリーク源なし
- **ブロッカー級**: CC フック（CCPet の append-event.mjs）が同梱されておらず、
  バッジ機能＝一番の差別化が一般環境で動かない（**配布成否を分ける最重要項目**）/
  未署名 exe の SmartScreen / LICENSE なし / net8.0 が 2026-11 EOL（.NET 10 LTS へ）
- **高優先**: 未処理例外ハンドラなし（黙って落ちる）/ ログ機構ゼロ /
  モニター構成変化未対応（モニター 0 枚で GetWidgetMonitor が throw → 起動クラッシュ経路）/
  シングルモニター環境で全画面常駐が使い物にならない
- **メモリ・CPU は健全**: 全走査 24MB/64ms 実測、タイマー類も負荷小。実測 Working Set を
  README に載せると安心材料になる

### セッションハイライト

- とも: 「このアプリケーションを広くVSCodeでCCを動かしている人に使ってもらいたい」
  → CC: 個人ツールから一般配布への方針転換。機能追加ではなく非機能要件（セキュリティ・
  メモリ・堅牢性）の監査を先に実施する流れに
- とも: 「CCPetの名残が残ってたのね」
  → CC: 名残というより「フック基盤が CCPet 側にあって同梱されていない」外部依存の構図と整理。
  監査の最大の発見が「コードの穴」ではなく「一番の差別化機能が外部依存で一般環境では動かない」
  だった点は象徴的
- とも: 「シングルモニター対応は、ともさんも思ってました。CCPetみたいに、最前面に常駐する
  ウインドウで、自由にサイズを変えられる（縦長とか横長とか）タイプにブラッシュアップできたら便利」
  → CC: ウィンドウモードを Phase 6 筆頭に登録。既存実装が LayoutUpdated / SizeChanged で
  サムネイル矩形を追従させる作りなので、リサイズ対応は大工事にならない見込み

### 次のセッションで最初にやること
1. TODO.md / HANDOFF.md の変更をコミット（未コミット）
2. Phase 4 ①②に着手（WaitingPermission 固定解消＋PostToolUseFailure。
   `PurgeStale` のイベント駆動→時間駆動化もセットで、CcStateTracker 中心に 1 回で直す）

### 注意点・ブロッカー
- `.spec/TODO.md` の Phase 4〜6 追加と HANDOFF 追記は未コミット
- push が 403 になったら `gh auth switch --user bsfukushi`

---

## セッション 2026-07-10 14:19

### 使用ツール
Claude Code（VSCode 拡張 / Fable 5）

### 現在のタスクと進捗
- [x] 前セッションの未コミット分（TODO.md Phase 4〜6 / HANDOFF）をコミット＆プッシュ
- [x] v0.7.5 Phase 4 ①② — CC 状態バッジの堅牢化 → リリース・常駐入れ替え済み

### v0.7.5 Phase 4 ①②（死んだセッションの承認待ち固定＋PostToolUseFailure）

- ① 死んだセッションの `WaitingPermission` 固定を解消（`CcStateTracker.cs` / `MainWindow.cs`）:
  - `Resolve` に鮮度しきい値 `SilentAge`（1 時間）を追加。作業中・待ちは「現在進行」の
    主張なので、1 時間イベントが無ければ死んだとみなし代表選出から除外。
    **完了（Done）は除外しない** — 過去の事実で害がなく、アイドル中のセッション時計を
    消さないため。優先度で生きたセッションに必ず負けるので実害なし
  - `PurgeStale`（24h 掃除）がイベント駆動のみだったのを時間駆動化:
    tracker に `Sweep()` を公開し、MainWindow の 1 分間隔 DispatcherTimer から
    `Sweep()` → `ApplyCcStates()`。鮮度切れの反映もこのタイマーで拾う
- ② `post_tool_use_failure` を末端まで配線（ツール失敗・拒否時の承認待ち解除）:
  - `~/.claude/settings.json` に `PostToolUseFailure` フックを登録（新規セッションから有効）
  - CCPet の `append-event.mjs` VALID_TYPES に追加（フックは src 直呼びなので即有効。
    CCPet 本体は未対応だが eventsTail が invalid_type としてスキップするだけで無害 —
    パーサ実装を読んで確認済み。将来対応するなら event.ts の EVENT_TYPES と
    eventToStateId に追加、と append-event.mjs にコメントで残した）
  - `CcStateTracker.MapState` に `"post_tool_use_failure" => Working` を追加
- フック単体テスト済み（CCPET_EVENTS_FILE を scratchpad に向けて 1 行書けることを確認）
- publish して常駐プロセスを v0.7.5 に入れ替え済み（Working Set 実測 140MB）

### 次のセッションで最初にやること
1. v0.7.5 の常用観察: 死んだセッションの承認待ちバッジが 1 時間で消えるか /
   1 時間放置した正当な承認待ちバッジが消える副作用が気になるか（しきい値調整の材料）
2. Phase 4 残り: 未処理例外ハンドラ＋軽量ログ機構（セットで 1 コミットが自然）、
   モニター構成変化対応

### 注意点・ブロッカー
- `PostToolUseFailure` フックは settings.json 変更後の新規 CC セッションから有効。
  既存セッションでは発火しない可能性がある
- しきい値 `SilentAge`（1h）はハードコード。不満が出たら appsettings 化を検討
