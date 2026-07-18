# HANDOFF

> 直前の要約: `.agent/handoff/2026-07-10.md`

---

## セッション 2026-07-18 13:44

### 使用ツール
Claude Code（VSCode 拡張 / Fable 5）

### 現在のタスクと進捗
- [x] HANDOFF ロールオーバー（2026-07-10.md に 5 セッション分を要約）
- [x] 常用観察 8 日分のログヘルスチェック（読み取り警告 0 / events.jsonl 1.76MB でローテーション健在）
- [x] v0.10.1 DWM RPC の常時発行を停止＋ウォッチドッグのスリープ誤検知を根治 → リリース済み
- [x] SPEC §ウィンドウモード起案 → ヒアリング 3 問 → 承認（タスクバー風ストリップに修正）
- [x] v0.11.0 ウィンドウモード実装・実機検証・リリース → 常駐入れ替え済み（fullscreen 設定）
- [ ] README（日英）にウィンドウモード / displayMode の記載 — 未着手

### v0.10.1（DWM RPC 抑制＋ウォッチドッグ）

- ログ調査の結論: 毎日出ていた 1.1〜1.5 秒の Send 停止は **UpdateThumbnailRects が犯人ではない**
  （SlowIf 閾値 200ms の内訳ログが停止時刻に皆無 / 深夜 3〜4 時に集中 → スタンバイ復帰後の
  ページイン等が濃厚）。DWM RPC 抑制は予防的掃除として実施
- UpdateThumbnailRects: 適用済みの表示先矩形＋ソースサイズ（GetWindowRect — 相手を待たない）を
  キャッシュし、変化時だけ DWM を呼ぶ。定常状態の RPC 毎秒 2×枚数 → 0
- ウォッチドッグ: TickCount64 → QueryUnbiasedInterruptTime（サスペンド時間を除外）。
  7/15 の「90 万 ms 停止」偽記録を根治
- 7/15 19:22 の未処理例外は Windows シャットダウン時に .NET ランタイムが 10.0.9→10.0.10 に
  差し替わった既知パターン。コードの問題ではない

### v0.11.0（ウィンドウモード）

- `displayMode: "window"`（コード既定）/ `"fullscreen"`（従来）。**リポジトリの
  appsettings.json は fullscreen** ＝ ともさん環境（publish で上書きされても維持される）
- WindowChrome（CaptionHeight=0 / ResizeBorder 6px）で枠なし端リサイズ、上端 16px グリップ帯で
  ドラッグ移動、Topmost 既定 on、右クリックメニュー「最前面に固定 / 終了」（両モード共通）
- ストリップ: 縦長→1 列 / 横長→1 行（`GridColumnsFor`）。fullscreen は従来 ceil(√N) のまま
- 配置は `%LOCALAPPDATA%\VSCodeLiveTiles\window.json` に 1 秒デバウンス保存、
  復元時は作業領域クランプ（新規 `Services/WindowPlacementStore.cs`）
- 実機検証: 初期配置 / 保存・復元 / WS_EX_TOPMOST / 縦横切替（スクリーンショット目視）/
  fullscreen 不変（サブモニター 2560×1392・非 topmost）
- バグ 1 件を実装中に発見・修正: サムネイル矩形を `_root` 基準で計算していたため
  グリップ帯 16px 分はみ出し → 変換先をウィンドウ自身に（DWM はクライアント座標基準）

### セッションハイライト

- とも: 「シングルモードのときは、縦長タイル1列か横長タイル1行のどちらでいいとおもう。
  タスクバーと同じようなスタイル」
  → CC: 16:9 最適化の NxM グリッド案を破棄してストリップに単純化。仕様も実装も小さくなり、
  「fullscreen のグリッドをどうするか」の論点ごと消えた。ユーザーの一言が設計を削った好例
- とも: 「プレビュー画像がタイルからはみ出しています」（作業中の割り込み報告＋スクリーンショット付き）
  → CC: テスト起動中のウィジェットをともさんがその場で触って発見。グリップ帯 16px の
  座標基準ズレと即特定。**CC 自身がスクリーンショットを撮って修正後の見た目を目視検証**する
  ループで完結（縦 1 列・横 1 行の両方を画像で確認）
- CC: ログの「沈黙」が証拠になった — 停止時刻に SlowIf 内訳が 1 行も出ていないことから、
  計測済みコードは全員無罪と判定。容疑筆頭だった UpdateThumbnailRects を疑いから外し、
  修正の位置づけを「根治」から「予防的掃除」に格下げして着手した

### 次のセッションで最初にやること
1. ウィンドウモードの常用フィードバックを聞く（とも試用中: グリップ帯の掴みやすさ /
   200px 幅でのキャプション見切れ / 最前面の邪魔さ）
2. README（日英）にウィンドウモード・displayMode・右クリックメニューを追記
3. Phase 6 残り: VSCode 派生対応（Insiders/Cursor/VSCodium）/ UI 文字列の英語化 /
   GitHub Releases + winget

### 注意点・ブロッカー
- 常駐は v0.11.0 fullscreen（publish/ 運用）。window モードを試すには
  publish/appsettings.json の displayMode を "window" にして再起動
- window.json にはテスト時の配置（横長 1100×240）が残っている。window モード初回起動は
  その位置に出る（実害なし、動かせば上書き）
- dev ビルドの実行ファイルは `bin/Release/net10.0-windows/`（`win-x64` サブフォルダは
  publish の中間生成物で古いことがある — 今日それで v0.10.1 を誤起動した）
- push が 403 になったら `gh auth switch --user bsfukushi`
