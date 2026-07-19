using System.IO;
using System.Text.Json;

namespace VSCodeLiveTiles.Services;

/// <summary>
/// window モードの位置・サイズ（物理px）と最前面フラグ、擬似最大化の状態、ストリップの向き。
/// 追加フィールドは既定値付きなので、旧フォーマットの window.json もそのまま読める
/// （＝自動レイアウト・非最大化として解釈される）。
/// </summary>
public sealed record WindowPlacement(
    int X, int Y, int Width, int Height, bool Topmost,
    bool Maximized = false,
    int RestoreX = 0, int RestoreY = 0, int RestoreWidth = 0, int RestoreHeight = 0,
    string LayoutMode = "auto");

/// <summary>
/// %LOCALAPPDATA%\VSCodeLiveTiles\window.json への読み書き。
/// appsettings.json（ユーザーが書く設定）とアプリが書く状態を分ける。
/// 読み書きの失敗は握りつぶす — 壊れていたら初期配置に戻るだけで、常駐を止める理由にならない。
/// </summary>
public static class WindowPlacementStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VSCodeLiveTiles", "window.json");

    public static WindowPlacement? Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return null;
            var p = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
            return p is { Width: > 0, Height: > 0 } ? p : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WindowPlacement placement)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(placement));
        }
        catch
        {
        }
    }
}
