# HANDOFF

> 直前の要約: `.agent/handoff/2026-07-23.md`

---

## セッション 2026-07-26 15:17

### 使用ツール
Claude Code（VSCode 拡張 / Opus 5 1M）

### 現在のタスクと進捗
- [x] HANDOFF ロールオーバー（2026-07-23 → `2026-07-23.md`）
- [x] **バグ報告 → 原因特定 → 修正 → リリース（v0.14.1）**
- [ ] Tier B（③b outcome / ④ notification 誤 🔑）は未着手のまま

### v0.14.1 — `cd` でセッション帰属が飛びバッジが消えるバグ

**症状**（ともさん報告 15:15 / スクショ付き）: PropBoard が承認ダイアログを出しているのに
タイルにバッジも時計も一切出ない。

**根本原因**: フックが書く `cwd` は `process.cwd()`＝**Bash ツールの現在位置**であって
セッションのプロジェクトルートではない。`CcStateTracker` が全イベントの cwd で照合キーを
上書きしていたため、`cd ~` を挟んだ直後のイベントでセッションが `jimu`（ホーム）を名乗り、
キャプション `PropBoard` と照合できずバッジごと消えた。

**修正**: 照合キー（FolderName / ProjectName）は `session_start` で確定し、以後のイベントで
上書きしない（`IdentityFromStart`）。session_start を観測できなかったセッションのみ
後続イベントで暫定的に埋める。

### 調査の要点（同じ罠を踏まないために）
- **フックの取りこぼしではなかった**。`permission_request` は 15:14:23 に正常発火していた。
  最初 PowerShell の `Select-String | Select-Object -Last 40` がこの行を取りこぼして
  「フックが飛んでいない」と誤読しかけた → **events の突き合わせは Python で読むこと**
- 影響範囲: 全 248 セッション中 **36 件（約15%）** で cwd basename が途中変化
  （迷子先: drafts / scratchpad / marathon / mobile / android / .claude / jimu）
- **交差誤爆（他プロジェクトのタイルに出る）は 29,201 イベント中 0 件** →
  Tier B ④ の誤 🔑 はこれとは別問題。潜在リスクとしては残る

### 検証
- **ハーネス方式**: 本体の `CcStateTracker.cs` をそのままコンパイルする小さな console を作り、
  実 events.jsonl を 15:15:00 で打ち切って流して Red→Green を取得
  （修正前 `Resolve("PropBoard") -> null` / 修正後 `-> WaitingPermission since=15:14:23`）。
  他 5 タイルの解決結果は不変＝副作用なし
- **実機 E2E**: 自セッションで `cd ~` を踏ませ、イベントが `C:\Users\jimu` を名乗る状態で
  `SPEC.md - VSCodeLiveTiles` タイルが `15:16 ▸ 0:51　🔑 承認 0:03` を保持することをスクショで確認
- ビルド警告 0 / コミット・`git tag v0.14.1`・push・publish 常駐入れ替え（PID 35784）済み

### 次のセッションで最初にやること
1. v0.14.1 の常用フィードバック（バッジが消える現象が再発しないか）
2. Tier B の設計: ④ notification 弁別（**cwd 問題とは別原因**なので調査からやり直し）→ ③b outcome
3. Phase 6 積み残し（README 日英・VSCode 派生対応・UI 英語化）

### 注意点・ブロッカー
- dev 起動前に publish を止める（ミューテックスで黙って終了）
- events.jsonl の突き合わせは Python で読む（PowerShell の Select-String 経由は取りこぼした実績あり）
