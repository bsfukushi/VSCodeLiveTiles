# AGENTS.md - プロジェクト規約（不変ルール）

- 全AI（Claude Code・Gemini CLI・Codex 等）が参照する唯一の仕様書。
- このファイルは人間が管理する。AIは読み取り専用。絶対に変更しないこと。

---

## プロジェクト概要

| 項目 | 内容 |
|---|---|
| アプリ名 | VSCode Live Tiles（VSCode ライブタイル） |
| 種別 | Windows デスクトップウィジェット（常駐ランチャー） |
| 主要技術 | C# / WPF / .NET 10（net10.0-windows） |
| コア技術 | DWM Thumbnail API・WinEvent Hook・Win32 P/Invoke |
| 対象ユーザー | 開発者本人（一人用ツール） |

サブモニターに VSCode ウィンドウのライブ縮小表示をタイル状に並べ、
クリックでメインモニターへ移動・最大化するウィジェット。

---

## 技術スタック

- .NET 10（開発 SDK は 8/9/10 いずれでも可）、WPF（`UseWPF`）
- Nullable enable / ImplicitUsings enable
- UI はコードベース（XAML は App.xaml のみ、ウィンドウ・コントロールは C# で構築）
- 外部 NuGet 依存なし（Win32 API は自前 P/Invoke: `Interop/`）
- 設定: `appsettings.json`（System.Text.Json で読み込み）

---

## ビルド・実行コマンド

| 操作 | コマンド |
|---|---|
| ビルド | `dotnet build -c Release` |
| デバッグ起動 | `dotnet run --project src/VSCodeLiveTiles` |
| 発行（単体 exe） | `dotnet publish src/VSCodeLiveTiles -c Release -r win-x64 --self-contained false -o publish` |

---

## ファイル構成

```
src/VSCodeLiveTiles/
├── App.xaml / App.xaml.cs      # エントリポイント
├── MainWindow.cs               # ウィジェット本体（コードベース UI）
├── AppConfig.cs                # appsettings.json の型
├── Controls/ThumbnailTile.cs   # ライブタイル 1 枚分のコントロール
├── Services/
│   ├── WindowTracker.cs        # 対象ウィンドウの列挙・追跡
│   └── MonitorService.cs       # モニター列挙・座標計算
└── Interop/
    ├── NativeWindows.cs        # Win32 P/Invoke 定義
    ├── DwmThumbnail.cs         # DWM Thumbnail API ラッパー
    └── WinEventHook.cs         # WinEvent フック
```

---

## 命名規則（C# 標準）

- クラス・メソッド・プロパティ・public フィールド: PascalCase
- ローカル変数・引数: camelCase
- private フィールド: `_camelCase`
- 定数: PascalCase（C# 慣習）
- P/Invoke 定義は `Interop/` に集約し、Win32 の元の名前を維持する

---

## コーディング規約

- Nullable enable を維持。`!`（null 免除）は根拠がある場合のみ
- UI はコードベース C# で構築する（新規 XAML ファイルは追加しない）
- 座標は物理ピクセルで扱う（Per-Monitor v2 DPI）
- DWM サムネイルの登録・解除はライフサイクルを対で管理する（リーク防止）

---

## AI作業ルール（全AI共通）

1. 変更後は `dotnet build` でビルドが通ることを確認する
2. Win32 API を新規に使う場合は Interop/ に定義を追加し、既存の書式に合わせる
3. 非同期・イベント絡みの修正は、タイミング・状態遷移を通しで確認してから 1 回で直す
4. 不明な前提・影響範囲があれば、実装前に確認する

---

## 禁止事項（Boundaries）

- AGENTS.md をAIが変更しない
- ユーザーの明示的な指示なくファイルを削除しない
- bin/ obj/ publish/ 配下を直接編集しない

---

## Git運用

- 一人開発・main 直コミット運用（ブランチ必須ではない）
- コミットメッセージ: 日本語、Conventional Commits 風（feat: / fix: / refactor:）

---

_最終更新：2026-07-10_
