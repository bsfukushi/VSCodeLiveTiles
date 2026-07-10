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
