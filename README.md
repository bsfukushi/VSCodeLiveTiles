# VSCode Live Tiles

サブモニターに VSCode ウィンドウのライブ縮小表示をタイル状に並べ、
クリックするとメインモニターへ移動して最大化するランチャーウィジェット。

タスクバーのホバープレビューと同じ **DWM Thumbnail API** を使うため、GPU 合成で
リアルタイム・低負荷。

## 動作要件

- Windows 10 / 11
- .NET 8 デスクトップランタイム（開発は SDK 8/9/10 いずれでも可）

## ビルド & 実行

```bash
cd S:/Tools/VSCodeLiveTiles
dotnet build -c Release
dotnet run --project src/VSCodeLiveTiles          # デバッグ起動
# もしくは発行して単体 exe 化
dotnet publish src/VSCodeLiveTiles -c Release -r win-x64 --self-contained false -o publish
# → publish/VSCodeLiveTiles.exe を起動
```

起動すると **サブモニター（最初の非プライマリ）** に全画面で常駐し、開いている
VSCode ウィンドウをライブタイルで並べる。タイルをクリックすると、その VSCode が
**メインモニター（プライマリ）で最大化・最前面化**される。

終了はウィジェットを閉じる（Alt+F4）。

## 設定（`appsettings.json`）

| キー | 既定 | 意味 |
|---|---|---|
| `targetProcessNames` | `["Code"]` | 対象プロセス名（拡張子なし）。増やせば他アプリも対象に |
| `widgetMonitorIndex` | `null` | ウィジェットを載せるモニター。`null`=最初の非プライマリ。番号で明示指定可 |
| `targetMonitorIndex` | `null` | クリックで最大化する先。`null`=プライマリ。番号で明示指定可 |
| `refreshIntervalMs` | `1500` | WinEvent の保険ポーリング間隔。`0` で無効 |
| `captionSuffixesToStrip` | `[" - Visual Studio Code", …]` | キャプションから除去する末尾 |

モニター番号は `EnumDisplayMonitors` の列挙順（0 始まり）。狙いと違う画面に出たら
`widgetMonitorIndex` / `targetMonitorIndex` を数値で指定する。

## CC 状態バッジ（v0.6〜）

Claude Code のフック（CCPet 由来、グローバル `~/.claude/settings.json` で稼働中）が
追記する `~/.ccpet/events.jsonl` を tail し、各タイルに CC の状態を表示する。

| バッジ | 状態 | 表示 |
|---|---|---|
| ❓ 質問（黄） | AskUserQuestion で停止中 | 経過時間付き＋枠が明滅 |
| 🔑 承認（橙） | 承認ダイアログ・通知で停止中 | 経過時間付き＋枠が明滅 |
| ✔ 完了（緑） | 応答完了（次の入力まで残る） | バッジのみ |
| ● 作業中（青） | 実行中 | バッジのみ |

- タイルとの対応付けはキャプションが `cwd` のフォルダ名で終わるかで判定。
  マッチしないタイルはバッジなし（誤表示より無表示）
- `~/.ccpet/` が無い環境ではバッジ機能だけ静かに無効化される（他機能に影響なし）
- 読み取り専用（events.jsonl には一切書き込まない）。パスは `CCPET_EVENTS_FILE` で上書き可

## 設計メモ

- DWM サムネイルは登録先ウィンドウの**描画内容の上**に合成されるため、キャプション/枠は
  サムネイル領域の外（タイル下段の帯）に置いている
- サムネイルは入力を奪わないので、クリックはサムネイル子ウィンドウの `WM_LBUTTONUP` で拾う
- 最小化中のウィンドウはサムネイルが空になるため、タイルを純 WPF のプレースホルダに切替
- タブ切替（タイトル変化）ではサムネイルを作り直さず、キャプションのみ更新（ちらつき防止）
- Per-Monitor v2 DPI 対応。座標は物理ピクセルで扱う

## スコープ

VSCode 限定・自動グリッド・クリックで移動＋最大化（v0.4）、CC 状態バッジ（v0.6）までを実装済み。
他アプリ対応は `targetProcessNames` を増やせば動く作りにしてある。
