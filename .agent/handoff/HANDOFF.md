# HANDOFF

> 直前の要約: `.agent/handoff/2026-07-20.md`

---

## セッション 2026-07-23 07:08

### 使用ツール
Claude Code（VSCode 拡張 / Opus 4.8 1M）

### 現在のタスクと進捗
- [x] HANDOFF ロールオーバー（2026-07-19〜20 を `2026-07-20.md` に要約）
- [x] project-hub のループエンジニアリング相談アドバイス 4 点を検討 → Tier A（低リスク3点）を実装・検証・リリース（v0.14.0）
- [ ] Tier B（③b outcome バッジ / ④ notification 誤 🔑）は設計相談してから別セッション

### v0.14.0（ループエンジニアリング可視化 Tier A）
- **① 並列度バッジ**: running 本数を bool→int で運び、作業中を `● N`（本数不明は `● 作業中`）。
  本数は stop イベントにしか乗らないので他 Working 契機で 0 にリセット（stale な数字を残さない）。
  `CcEventRecord.RunningBackgroundTasks` / `Session.RunningTasks` / `Resolve` が本数を返す
- **② ホットパス read 除去**: `append-event.mjs` が post_tool_use / post_tool_use_failure で
  projectName 解決（AGENTS.md read）をスキップ。reader は projectName を sticky 保持し null で
  上書きしないので照合に影響なし
- **③a 完了経過**: `✔ 完了 N分`（放置度・秒は出さず粗く）。`RefreshElapsed` に Done を含め、
  `UpdateBadge` に Text 差分ガード（同値再描画を回避）

### 検証（隔離 events ファイル＋実アプリ＋スクショ）
- publish 常駐は停止済みだったのでミューテックス衝突なし。`VSCODE_LIVE_TILES_EVENTS_FILE` を
  homedir 配下のテストファイルに向け、フック経由で合成イベントを注入 → dev(Release) で目視
- 確認: `● 3`（stop running×3）/ post_tool_use で `● 作業中` へリセット / `✔ 完了 0分`（クロック
  0:04→0:11 進行で経過描画も動作）/ ② は node 単体で projectName スキップ/解決を確認
- ※ 生ラインで JSON を書くとき cwd の `\` は `\\` にエスケープが要る（`S:\Tools` は無効エスケープで
  reader がスキップ）。合成注入はフック経由（JSON.stringify）が安全

### ④ の重要データ
- **ともさん確認済み: 承認ダイアログが出ていないのにマゼンタ（承認待ち）が明滅する誤 🔑 の実害あり**。
  現状は SPEC §2（notification＝承認待ち）どおりの挙動だが、実害が出ているので Tier B で弁別を実装する価値あり

### 次のセッションで最初にやること
1. v0.14.0 の常用フィードバック（`● N` の見やすさ・完了経過の有用性）
2. Tier B の設計: ④ notification 弁別（payload に何を残すか／CC Notification の弁別材料調査）→ ③b outcome
3. Phase 6 積み残し（README 日英・VSCode 派生対応・UI 英語化）

### 注意点・ブロッカー
- 常駐は v0.14.0 publish に入れ替え（fullscreen）
- dev 起動前に publish を止める（ミューテックスで黙って終了）
- push が 403 になったら `gh auth switch --user bsfukushi`
