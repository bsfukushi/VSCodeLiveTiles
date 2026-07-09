# TODO.md - タスク分解

> SPEC.md を元にAIが分解する。人間の承認後に確定。
> ステータス: [ ] 未着手 / [~] 作業中 / [x] 完了 / [-] スキップ

## バージョン

> **現在: v0.6.1**（`src/VSCodeLiveTiles/VSCodeLiveTiles.csproj` の `<Version>` が正本）

| バージョン | マイルストーン |
|---|---|
| v1.0.0 | 常用開始 — 日常のワークフローに組み込んで安定稼働 |
| v2.0.0 | （常用後に決める。候補: 他アプリ対応の本格化・設定UI・タスクトレイ常駐） |

---

## Phase 1: CC 状態バッジ（v0.6.0 / SPEC §CC状態バッジ 承認済み 2026-07-07）

### Step 1-1: イベント読み取り基盤
- [x] `Services/CcEventsReader.cs` — events.jsonl の tail リーダー
      （起動時に末尾 512KB 再構築 / FileSystemWatcher + 1秒保険ポーリング / ローテーション検知 / 壊れ行スキップ）
- [x] `Services/CcStateTracker.cs` — sessionId→状態の機械 + cwd/projectName 照合 + ウィンドウ代表状態
- [x] ビルド確認

### Step 1-2: タイル UI
- [x] `ThumbnailTile` にバッジ（キャプション帯右端: 色ドット+ラベル+経過時間）と待ちグロー
      （BorderThickness 2 固定・色の明滅のみ）を追加
- [x] `MainWindow` 配線（リーダー/トラッカー生成・状態配布・経過時間タイマー・破棄）
- [x] events.jsonl 不在時はバッジ機能を静かに無効化することを確認（StartCcBadges の false/例外経路）
- [x] ビルド確認

### Step 1-3: 仕上げ
- [x] コードレビュー（8観点並列 → 検証 → 4件修正: ts検証・catchのDispose対象・未知イベントのLastTs更新・ポーリング前の長さチェック）
- [x] 実機動作確認（承認バッジの表示・照合を実機で確認。視認性 fix v0.6.1 も確認済み 2026-07-07）
- [x] README 更新・csproj バージョン 0.6.0
- [x] コミット + `git tag v0.6.0`
