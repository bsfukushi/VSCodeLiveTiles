# TODO.md - タスク分解

> SPEC.md を元にAIが分解する。人間の承認後に確定。
> ステータス: [ ] 未着手 / [~] 作業中 / [x] 完了 / [-] スキップ
> 完了済み Phase 1〜5 は `.spec/archive/TODO-phase1-5-2026-07-18.md` に切り出し済み。

## バージョン

> **現在: v0.14.0**（`src/VSCodeLiveTiles/VSCodeLiveTiles.csproj` の `<Version>` が正本）

| バージョン | マイルストーン |
|---|---|
| v1.0.0 | 常用開始 — 日常のワークフローに組み込んで安定稼働 |
| v2.0.0 | （常用後に決める。候補: 他アプリ対応の本格化・設定UI・タスクトレイ常駐） |

---

## Phase 6: 間口拡大

- [x] v0.11.0 **ウィンドウモード**（SPEC §ウィンドウモード 承認 2026-07-18）:
      `displayMode: "window"`（コード既定）で枠なし・最前面・自由リサイズの小型ウィンドウ。
      WindowChrome（CaptionHeight=0 / ResizeBorder 6px）＋上端グリップ帯ドラッグ。
      タスクバー風ストリップ（縦長→1列 / 横長→1行）。配置・最前面フラグは
      `%LOCALAPPDATA%\VSCodeLiveTiles\window.json` に 1 秒デバウンス保存、
      復元時は作業領域クランプ。リポジトリの appsettings.json は fullscreen
      （＝ともさん環境。publish で上書きされても維持される）。
      実機検証済み: 初期配置 / 復元 / topmost / 縦横切替 / 保存 / fullscreen 不変。
      サムネイル矩形の基準を _root → ウィンドウに修正（グリップ帯 16px 分のはみ出し fix）
- [ ] VSCode 派生対応: `Code - Insiders` / `Cursor` / `VSCodium` を既定の
      `targetProcessNames` / `captionSuffixesToStrip` に追加
- [ ] UI 文字列の英語化（「最小化中」「質問/承認/完了/作業中」等のハードコード解消）
- [x] v0.11.0 右クリックメニューに「終了」「最前面に固定」（両モード共通。
      ウィンドウモード実装と同時に消化）
- [ ] README（日英）にウィンドウモード / displayMode / 右クリックメニューを追記
- [ ] GitHub Releases でバイナリ配布 ＋ winget マニフェスト登録（未署名 SmartScreen 対策の現実解）

---

## Phase 7: 常用フィードバック第2弾（PLAN 2026-07-18 / SPEC 承認済み）

### Step 7-1: バッジ視覚調整（v0.11.1）✅ 2026-07-18 リリース済み

**ゴール**: 承認待ちタイルがマゼンタのドット・ラベル・視覚幅 7px の明滅リングで表示され、
質問待ち（黄）と瞬間視で区別できる。通常・作業中タイルの見た目とサムネイル矩形は変更前と
1px も変わらず、待ち解除（前面化）でリングが消えてもサムネイルは動かない。

- [x] `ThumbnailTile.cs` — `PermissionColor` を `#D19A66` → `#E0409A`（マゼンタ）に変更
- [x] `ThumbnailTile.cs` — 外周リング構造: Margin 6 → Margin 1＋常設透明リング 5px＋既存枠 2
      （オフセット合計 8px 不変）。待ちグローの適用先をリング＋枠の両方に、角丸は CornerRadius(9)/(4) で同心
- [x] ビルド確認（警告 0）
- [x] コードレビュー（bug 0 / warn 1: リング領域のヒットテスト拡大 → クリックの的が広がる
      意図的挙動として受容、Transparent→null 禁止コメントで防御）
- [x] 動作確認 — 枠色 #3F3F46 のピクセル座標がビフォー/アフターで縦横とも完全一致（矩形不変）/
      マゼンタバッジ表示（通常帯・青帯とも）/ 合成 permission_request でリング明滅を確認
      （同一画素 D03C8F↔AF3579 の輝度変化）/ session_end で消灯確認。
      最終色はとも確認済み（2026-07-18「視認性が格段に良くなりました」— チューニング不要）
- [x] バージョン 0.11.1 ＋ コミット ＋ `git tag v0.11.1` ＋ publish 常駐入れ替え ＋ push 済み

### Step 7-2: タイルのドラッグ並べ替え（v0.12.0）

**ゴール**: タイルをドラッグすると挿入方式で並び替わり、ドラッグ閾値未満のクリックは従来どおり
ウィンドウ切り替えになる（誤爆しない）。新規ウィンドウは末尾に付き、途中のウィンドウを閉じても
残りの並びは保たれる。Esc・枠外ドロップで元の並びに戻る。fullscreen / window 両モードで動く。

- [x] `MainWindow.cs` — 手動並びリスト（メモリ上・HWND キー）導入。`OnWindowsUpdated` の
      整列を HWND 昇順 → リスト順に変更（新規は末尾追加 / 消えたものはタイルのみ除去）。
      死んだ HWND は上限 64 件で古い順に間引く（`PruneOrder`）
- [x] ドラッグ検知 — グリッドの Preview（トンネル）で拾い、閾値超えでドラッグ開始。
      ドラッグした押下は MouseUp を `Handled` にしてタイルのクリック処理へ通さない
      （`_draggedSincePress`。Esc で畳んだ後の離しも含む）
- [x] ドラッグ中の並び反映 — 掴んだタイルを半透明化（WPF の Opacity は DWM 合成に効かないため、
      `DwmThumbnail.UpdateDestination` に opacity 引数を追加してサムネイル本体も薄くする）。
      Esc / ウィジェット外ドロップでキャンセル（掴む前の位置に復元）
- [x] ビルド確認（警告 0）
- [x] コードレビュー（bug 0 / 指摘 8 件のうち 6 件を反映）:
      HIGH `_draggedSincePress` の取り残し（キャプチャを奪われて Up が届かないと次のクリックを
      1 回飲み込む）→ 押下ごとにリセット / MEDIUM キャプチャ喪失を確定ではなくキャンセル扱いに /
      MEDIUM ドラッグ中は `PruneOrder` を止める（`_dragOriginalIndex` の添字ズレ防止）/
      キャンセル時の DWM 余分な 1 往復を解消 / 不透明度を `ThumbnailTile.DragOpacity` に一本化 /
      コメント修正。未対応: HWND 再利用で末尾ではなく旧位置に出る可能性（永続化なし仕様のため許容）
- [x] 動作確認 — 実機チェック済み（合成マウス入力＋スクリーンショット比較）:
      前方向/後方向の挿入 / Esc キャンセル / 枠外ドロップ / クリック切替の誤爆なし /
      新規ウィンドウは末尾 / 途中のウィンドウを閉じても並び保持 / 再起動で HWND 順に戻る /
      window（縦1列・横1行）と fullscreen（3×3 の行またぎ）両モード /
      ドラッグ中もバッジ・時計の更新継続
      ※ 検証中に発見・修正: Esc で畳んだ後にボタンを離すと、その位置のタイルが
      クリック扱いになりウィンドウが切り替わっていた
- [x] バージョン 0.12.0 ＋ コミット ＋ `git tag v0.12.0` ＋ publish 常駐入れ替え

### Step 7-4: 右クリックメニューの拡張（v0.13.0）✅ 2026-07-20 リリース済み

**ゴール**: window モードの右クリックメニューから、最大化のトグルと
ストリップの向き固定（自動 / 縦1列 / 横1行）ができる。fullscreen では両方とも出ない。

- [x] `WindowPlacementStore.cs` — `WindowPlacement` に `Maximized` / `Restore{X,Y,Width,Height}` /
      `LayoutMode` を既定値付きで追加（旧 window.json もそのまま読める）
- [x] `MainWindow.cs` — メニュー構築を window / fullscreen で分岐。レイアウトは 3 択のラジオ相当、
      最大化はチェック可能なトグル
- [x] `MainWindow.cs` — 擬似最大化（`WindowState.Maximized` は使わず作業領域へ `SetWindowPos`。
      枠なしウィンドウを本当に最大化するとタスクバーを覆うため）。解除で直前の矩形へ復帰。
      最大化中にリサイズされたら `SyncMaximizedState` でチェックを自動解除
- [x] `GridColumnsFor` — `StripLayout.Auto/Vertical/Horizontal` を反映（fullscreen は ceil(√N) のまま）
- [x] ビルド確認（警告 0）
- [x] 動作確認 — UI Automation でメニュー項目を名前指定で Invoke して検証:
      縦1列（横長ウィンドウでも1列）/ 横1行（縦長ウィンドウでも1行）/ 最大化 →作業領域
      （1920,0 2560x1392 ＝タスクバーを覆わない）/ 解除→直前の矩形へ復帰 /
      最大化中のリサイズでチェック自動解除 / 再起動後もレイアウト・配置・最前面を復元
- [x] バージョン 0.13.0 ＋ コミット ＋ `git tag v0.13.0` ＋ publish 常駐入れ替え

### Step 7-5: ループエンジニアリング可視化 Tier A（v0.14.0）✅ 2026-07-23 リリース済み

> project-hub のループエンジニアリング相談で得たアドバイス 4 点のうち、低リスクな 3 点を実装。

- [x] **① 並列サブエージェント数の可視化**: `slimBackgroundTasks` が既に書いている running 本数を
      bool に潰さず int で運ぶ。`CcEventRecord.RunningBackgroundTasks`(int) / `Session.RunningTasks` /
      `Resolve` が本数を返す。作業中バッジを `● N`（N>0）／不明時は `● 作業中`。
      本数は stop イベントにしか乗らないので、他の Working 契機（post_tool_use 等）では 0 にリセット
      （stale な数字を残さない）
- [x] **② post_tool_use のホットパスから同期 read 除去**: `append-event.mjs` で
      post_tool_use / post_tool_use_failure のとき projectName 解決（AGENTS.md read）をスキップ。
      reader は projectName を sticky 保持し null で上書きしないので照合に影響なし
- [x] **③a 完了バッジに経過表示**: `✔ 完了 N分`（放置度）。緊急性はないので秒は出さず粗く。
      `RefreshElapsed` に Done を含め、`UpdateBadge` に Text 差分ガードを追加（同値再描画を回避）
- [x] ビルド確認（警告 0）
- [x] 動作確認 — 隔離 events ファイル＋実アプリ＋スクショ:
      `● 3` 表示 / post_tool_use で `● 作業中` へリセット / `✔ 完了 0分`（クロック進行で経過描画も確認）/
      ② は node 単体で post_tool_use→projectName:null・session_start/stop→解決を確認
- [x] バージョン 0.14.0 ＋ コミット ＋ `git tag v0.14.0` ＋ publish 常駐入れ替え

### Step 7-6: ループエンジニアリング可視化 Tier B（設計相談 → 実装）

> フック機構の追加・仕様変更を伴うため、着手前に設計を固める。

- [ ] **③b outcome（成功/失敗）バッジ**: 今のバッジは liveness のみで outcome を映さない。
      Stop hook で差分テスト結果を判定し、✔緑（成功）/ ✘赤（失敗）を分ける。
      「成功の定義（何の exit code か）」「結果の出所」から設計が必要
- [ ] **④ notification の誤 🔑 弁別**: CC の Notification はアイドル通知（60秒放置）でも発火し、
      承認待ちでないのにマゼンタが明滅し得る。**ともさん確認済み: 誤明滅の実害あり（2026-07-23）**。
      現状はフックが payload を background_tasks 以外捨てているため、種別弁別には
      notification payload の弁別材料を残す改修＋reader 追加＋SPEC §2 の仕様変更が要る

### Step 7-3: Phase 7 完了確認

- [ ] Phase 全体のコードレビュー（横断レビュー — リング構造とドラッグの相互作用含む）
- [x] レビュー指摘の既存問題: 明滅中のタイルがウィンドウクローズで除去されると StopGlow が
      呼ばれずアニメーションクロックが延命し得る → v0.12.0 のタイル除去パスで
      `SetCcState(CcState.None, ...)` を呼ぶようにして解消
- [ ] 動作確認（常用フィードバック: マゼンタの視認性・ドラッグの使い勝手をともさんに確認）
- [ ] HANDOFF / README 反映（並べ替え操作の記載）
