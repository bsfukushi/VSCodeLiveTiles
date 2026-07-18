using System.IO;
using System.Text.Json;

namespace VSCodeLiveTiles;

/// <summary>
/// appsettings.json を読み込む軽量な設定モデル。
/// キー名は JSON と一致。"// ..." 形式のコメントキーは無視される。
/// </summary>
public sealed class AppConfig
{
    public string[] TargetProcessNames { get; init; } = new[] { "Code" };
    public int? WidgetMonitorIndex { get; init; }
    public int? TargetMonitorIndex { get; init; }
    public int RefreshIntervalMs { get; init; } = 1500;
    public string[] CaptionSuffixesToStrip { get; init; } =
        new[] { " - Visual Studio Code", " — Visual Studio Code" };

    /// <summary>"window"（枠なし小型ウィンドウ・既定）| "fullscreen"（サブモニター全画面常駐）。</summary>
    public string DisplayMode { get; init; } = "window";

    public bool IsFullscreen
        => string.Equals(DisplayMode, "fullscreen", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg is not null)
                    return cfg;
            }
        }
        catch
        {
            // 設定が壊れていても既定値で動く（起動を止めない）
        }
        return new AppConfig();
    }
}
