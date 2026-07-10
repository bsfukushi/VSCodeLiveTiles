# TODO.md - タスク分解

> SPEC.md を元にAIが分解する。人間の承認後に確定。
> ステータス: [ ] 未着手 / [~] 作業中 / [x] 完了 / [-] スキップ

## バージョン

> **現在: v0.7.5**（`src/VSCodeLiveTiles/VSCodeLiveTiles.csproj` の `<Version>` が正本）

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

### Step 1-3: 仕上げ（Phase 2 は下部）
- [x] コードレビュー（8観点並列 → 検証 → 4件修正: ts検証・catchのDispose対象・未知イベントのLastTs更新・ポーリング前の長さチェック）
- [x] 実機動作確認（承認バッジの表示・照合を実機で確認。視認性 fix v0.6.1 も確認済み 2026-07-07）
- [x] README 更新・csproj バージョン 0.6.0
- [x] コミット + `git tag v0.6.0`

---

## Phase 2: 常用フィードバック反映（2026-07-09）

実機で常用して見つかった不具合と要望。すべて実データ（events.jsonl）で原因を特定してから修正。

- [x] v0.6.2 承認待ちの明滅を前面化で止める
      （CC に「承認した瞬間」のフックがない。遅延の正体はツール実行時間）
- [x] v0.6.3 代表状態の優先度 `Done < Working`
      （終わったセッションの「完了」が動いているセッションの「作業中」を上書きしていた）
- [x] v0.6.4 `stop` かつ背景タスク running なら「作業中」
      （サブエージェント・`run_in_background` の Bash が残っているのに「完了」が出ていた）
- [x] v0.7.0 セッション時計（開始時刻 ▸ 経過）
      （`session_start` は末尾 512KB の外にあるため全体走査で拾う）
- [x] v0.7.1 セッション時計のフォントを状態バッジと同じ 20px に（16px は小さかった）
- [x] v0.7.2 多重起動時に 2 つ目のインスタンスがクラッシュする問題を修正
      （`initiallyOwned: true` で所有権を得ていないのに `ReleaseMutex` を呼んでいた。
      あわせて「既に起動しています」の MessageBox を廃止し、黙って終了する）
- [x] publish し直して常駐プロセスを v0.7.2 に入れ替え
- [x] 実機動作確認（時計の表示・二重起動の抑止をユーザーが目視 / 検証コマンドで確認）

## Phase 3: 切り替え UX 改善 + GitHub 公開（2026-07-10）

- [x] GitHub リモート接続（https://github.com/bsfukushi/VSCodeLiveTiles）— main + 全タグをプッシュ
- [x] v0.7.3 クリック切り替え時の約10秒フリーズを解消
      （同期 Win32 呼び出しが相手の応答待ちで UI スレッドごとブロック。
      ShowWindowAsync / SWP_ASYNCWINDOWPOS で非同期化、AttachThreadInput 廃止）
- [x] v0.7.4 切り替え時のガチャつく再描画を解消
      （現在地を見ずに毎回 復元→移動→最大化 していた。対象モニターで最大化済みなら前面化だけに）
- [x] publish し直して常駐プロセスを v0.7.4 に入れ替え
- [x] 実機動作確認（ユーザー確認済み: 「きれいにストレスなく切り替わる」）

## Phase 4: 品質基盤 — 配布前提の堅牢化（検証レポート 2026-07-10）

> 配布しなくても自分が得をする改善。旧「積み残し」2件もここに吸収。

- [x] v0.7.5 死んだセッションが `WaitingPermission` のまま残ると 24 時間そのタイルが承認待ちに固定される
      （`Resolve` で 1 時間イベントの無い作業中・待ちセッションを代表選出から除外。
      完了は過去の事実＋セッション時計保持のため残す。あわせて MainWindow に 1 分間隔の
      掃除タイマーを追加し、`PurgeStale` をイベント駆動→時間駆動に）
- [x] v0.7.5 `PostToolUseFailure` を拾っていないため、ツール失敗・拒否時に承認待ちが残る
      （3 点セットで配線: settings.json に PostToolUseFailure フック登録 /
      CCPet append-event.mjs の VALID_TYPES に post_tool_use_failure 追加 /
      CcStateTracker で Working にマッピング。CCPet 本体は未対応だが invalid_type スキップで無害）
- [ ] 未処理例外ハンドラ（`DispatcherUnhandledException` / `AppDomain.UnhandledException`）
      → 黙って消えるのをやめ、クラッシュログを残す
- [ ] 軽量ログ機構（`%LOCALAPPDATA%\VSCodeLiveTiles\logs\`。例外＋起動時の環境サマリーのみ）
- [ ] モニター構成変化対応（`SystemEvents.DisplaySettingsChanged` で再配置。
      モニター 0 枚（RDP 切断中など）で `GetWidgetMonitor` が throw → 起動クラッシュする経路を塞ぐ）

## Phase 5: 配布準備

- [ ] LICENSE 追加（MIT 想定。公開済みリポジトリなので早めに）
- [ ] .NET 10 LTS へ移行（net8.0 は 2026-11 EOL）＋ self-contained 発行（単体 exe 約70MB）
- [ ] CC フックの同梱＋セットアップ手段（現状 CCPet の `append-event.mjs` 前提で同梱なし。
      バッジ機能＝一番の差別化が一般環境で体験されない。**配布成否を分ける最重要項目**）
- [ ] README 英語版＋「ネットワーク通信ゼロ・テレメトリゼロ・読み取り専用」の明記
- [ ] 実測メモリ（Working Set）・CPU 負荷を README に記載（常駐ツールの最初の質問対策）

## Phase 6: 間口拡大

- [ ] **ウィンドウモード**（とも要望 2026-07-10）: 全画面常駐をやめ、CCPet のような
      最前面・自由リサイズのウィンドウに。縦長→1列 / 横長→1行 など縦横比でグリッド自動調整。
      シングルモニター環境（一般ユーザーの過半）への対応がこれで解決する
- [ ] VSCode 派生対応: `Code - Insiders` / `Cursor` / `VSCodium` を既定の
      `targetProcessNames` / `captionSuffixesToStrip` に追加
- [ ] UI 文字列の英語化（「最小化中」「質問/承認/完了/作業中」等のハードコード解消）
- [ ] 右クリックメニューに「終了」（現状 Alt+F4 のみ）
- [ ] GitHub Releases でバイナリ配布 ＋ winget マニフェスト登録（未署名 SmartScreen 対策の現実解）
