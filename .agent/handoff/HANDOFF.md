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

### セッション終了時の状態
- v0.14.1 を publish 常駐に入れ替え済み。**ともさんが 2026-07-26 16:10〜 常用テスト中**。
  次のセッションはまずこのテスト結果を聞くところから始める

### 次のセッションで最初にやること
1. **v0.14.1 の常用テスト結果をともさんに確認**（バッジが消える現象が再発しないか）
2. Tier B の設計: ④ notification 弁別（**cwd 問題とは別原因**なので調査からやり直し）→ ③b outcome
3. Phase 6 積み残し（README 日英・VSCode 派生対応・UI 英語化）

### 注意点・ブロッカー
- dev 起動前に publish を止める（ミューテックスで黙って終了）
- events.jsonl の突き合わせは Python で読む（PowerShell の Select-String 経由は取りこぼした実績あり）

---

## セッション 2026-07-31 12:56

### 使用ツール
Claude Code（VSCode 拡張 / Opus 5 1M）

### 現在のタスクと進捗
- [x] **新PC移行後の環境チェック**（データを新PCへ引っ越して起動した直後）
- [x] **.NET 10 の導入**（これが唯一の欠落。ビルドも publish 起動も不可だった）
- [x] **スタートアップ登録**（`shell:startup` に `VSCode Live Tiles.lnk`）
- [x] **v0.15.0 — タイルのタイトルを「プロジェクト名 - ファイル名」順に変更**
- [x] v0.14.1 の常用テスト結果確認 → **バッジ消失の再発なし**（ともさん報告）
- [ ] Tier B（③b outcome / ④ notification 誤 🔑）は引き続き未着手

### 新PC環境チェックの結果

`.NET 10 が入っていない` の一点だけが問題で、他は全て健全だった:

- リポジトリ clean・`efd4729` / tag `v0.14.1` まで同期済み
- フックインフラ健全（node v22.15.1 / `hooks/append-event.mjs` あり / events.jsonl に追記継続）。
  `~/.claude/settings.json` の 8 フックすべて `S:\Tools\VSCodeLiveTiles\hooks` を指して稼働
- モニター 2 枚（DISPLAY2=プライマリ / DISPLAY1=サブ）。`appsettings.json` は無修正で適合
- **`AGENTS.md` L25 の「開発 SDK は 8/9/10 いずれでも可」が誤り**だった。
  TargetFramework が `net10.0-windows` なので 8/9 では復元も通らない。
  ともさんが「開発 SDK は 10 必須」に修正（コミット `88685de`）

### .NET 10 導入でハマった経緯（次に同じ状況が来たとき用）

- `winget install Microsoft.DotNet.SDK.10` が **1618（別のインストールが進行中）** で連続失敗。
  リトライループを回しても解けなかった
- **原因は自分の winget ではなく、ともさんが手動起動していた
  `windowsdesktop-runtime-10.0.10-win-x64.exe` が MSI のグローバルロックを握っていたこと**。
  `Get-WinEvent -ProviderName MsiInstaller` で「クライアントプロセス ID: 9736」から特定した
- そのインストーラーは **「ファイル使用中」ダイアログで入力待ち**のまま 29 分停止していた。
  ダイアログをスクショして読んだ結果、**再起動要求ではなかった**（選択肢 A を選べば再起動不要）。
  掴んでいたのは **VSCode の C# Dev Kit 拡張**（`ms-dotnettools.csdevkit` の
  ProjectSystem BuildHost ＋ ServiceHost。どちらも .NET Host）
- 最終的にそのランタイム単体インストーラーは **`0x80070003` で失敗しロールバック**した。
  ログ上 `DotNetHost` / `DotNetHostFxr` は成功、`dotnet-runtime-10.0.10-win-x64.msi` だけが
  Package Cache から読めなかった（MSI エラー 2203）。**その MSI は今見ると正常に存在している**ので
  一時的なロック（リアルタイムスキャン等）の線が濃い
- **ロック解放後に winget 版 SDK 10.0.302 を入れたら再起動なしで一発で通った。**
  ランタイム 10.0.10（NETCore / WindowsDesktop / AspNetCore）も同時に入る

### v0.15.0 — タイトル順の変更で踏みかけた地雷

**要望**（とも）: 「ファイル名 - プロジェクト名」→「プロジェクト名 - ファイル名」。
ファイル未オープンのタイルはプロジェクト名だけが左端に出るので、位置関係が揃わないため。

**地雷**: `ThumbnailTile.CaptionText` は**表示テキストであると同時に CC バッジの照合キー**で、
`CcStateTracker.Matches` が `caption.EndsWith(プロジェクト名)` で判定している。
**素直に順序を入れ替えると全バッジが消える**（v0.14.1 で直したのとまったく同じ症状）。

**対応**: 表示文字列と照合キーを分離。`_matchKey`（並べ替え前＝プロジェクト名が末尾）を新設し
`CaptionText` はそちらを返す。`_caption.Text` だけ `ToProjectFirst()` で並べ替える。
**`CcStateTracker` は 1 行も変更していない。**

- 並べ替えは**最後の**区切りで割る（ファイル名側が ` - ` を含んでもプロジェクト名が壊れない）
- 区切りは半角ハイフン / em ダッシュ両対応（`captionSuffixesToStrip` の 2 種と揃えた）
- 区切り無し・前後どちらかが空なら並べ替えない（＝ファイル未オープンはそのまま左端）
- SPEC §4 / README（日英）の「キャプションがフォルダ名で終わる」を
  「照合は元の並びで行う」と書き直した（分離後は誤読を招くため）

### 試したこと・結果

- **成功**: MSI ロックの犯人特定に `Get-WinEvent -ProviderName MsiInstaller` →
  クライアントプロセス ID から辿る手順。プロセス名だけ見ても分からなかった
- **成功**: 止まっているインストーラーの `MainWindowHandle` を `CopyFromScreen` で撮って
  ダイアログ本文を読む。「再起動要求」という思い込みを実物で否定できた
- **失敗**: winget のリトライループ（30 秒 × 20 回）。ロックの持ち主が入力待ちで止まっている
  ケースでは永久に解けない。**リトライの前に「誰が握っているか」を先に見るべきだった**
- **失敗**: `Get-CimInstance Win32_Process` の `CommandLine` は昇格プロセスに対して空を返す。
  イベントログ経由のほうが早かった

### セッションハイライト

- とも: 「インストール中に再起動を求められたので、止めてました。」
  → CC: 1618 の犯人が自分の winget ではないと確定。ここでようやく「別のインストール」が
    実在すると分かり、MsiInstaller イベントログを辿る方向に切り替えた
- とも: 「キャンセルしちゃってOK?」
  → CC: 「技術的には安全だが得がない」と一度止めた。SDK でも同じ「ファイル使用中」に当たるので
    再起動は結局 1 回必要という読み。実際にはダイアログを撮って読んだ結果、
    **そもそも再起動要求ではなかった**ことが分かり、前提ごとひっくり返った
- とも: 「タイルのタイトルで、現在は、「ファイル名-プロジェクト名」になっていますが、
  「プロジェクト名-ファイル名」にしてください。ファイルを開いていないプロジェクトは、
  プロジェクト名だけ左揃えで表示されるので、位置関係が統一されます。」
  → CC: 実装に入る前に `CaptionText` が照合キー兼用であることに気づき、
    「単純に入れ替えるとバッジが全滅する」と先に報告してから表示／照合の分離を設計した。
    **見た目だけの要望が、5 日前に直したバグと同じ穴に通じていた**

### 次のセッションで最初にやること

1. **v0.15.0 の常用テスト結果をともさんに確認**（タイトル順・バッジが両立しているか）
2. Tier B ④ notification の誤 🔑 弁別（cwd 問題とは別原因なので調査からやり直し）→ ③b outcome
3. Phase 6 積み残し（README 日英のウィンドウモード追記・VSCode 派生対応・UI 英語化）

### 注意点・ブロッカー

- **`windowsdesktop-runtime-10.0.10-win-x64.exe`（ランタイム単体）はこの環境で `0x80070003` で失敗した。**
  .NET を入れ直す必要が出たら `winget install Microsoft.DotNet.SDK.10` を使う（SDK にランタイム同梱）
- **.NET のインストールは VSCode の C# Dev Kit と競合する。** 「ファイル使用中」が出たら
  選択肢 A（アプリを終了してから再起動します）で再起動不要。犯人は VSCode 本体ではなく
  `ms-dotnettools.csdevkit` の .NET Host 2 本
- **`ThumbnailTile.CaptionText` は CC バッジの照合キー。** 表示都合で中身を変えないこと。
  帯の見た目を変えるときは `_caption.Text` 側だけを触る
- dev 起動前に publish を止める（ミューテックスで黙って終了）
- events.jsonl の突き合わせは Python で読む（PowerShell の Select-String 経由は取りこぼした実績あり）
