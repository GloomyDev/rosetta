using System.Text.Json;
using Microsoft.Win32;

namespace Rosetta;

/// <summary>
/// Per-user state: the on/off toggles, the translation target and the start-with-Windows
/// registration. The fields are volatile because the scan thread reads them while the
/// tray (UI thread) writes them.
/// </summary>
internal static class Settings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rosetta");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Rosetta";

    /// <summary>Master switch: when false Rosetta does nothing at all.</summary>
    public static volatile bool Enabled = true;

    /// <summary>
    /// Online translation of selected text. This is the one feature that is NOT local:
    /// the selected text is sent to Google, so it is opt-in and off unless the user said
    /// otherwise (installer checkbox or tray).
    /// </summary>
    public static volatile bool Translate;

    /// <summary>Target language code for translation: "en", "ja" or "es".</summary>
    public static volatile string Target = "en";

    /// <summary>True once the user has seen the "text is sent to Google" notice.</summary>
    public static bool TranslateNoticeShown { get; set; }

    public static void Load()
    {
        bool? translate = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("enabled", out var e)) Enabled = e.GetBoolean();
            if (root.TryGetProperty("translate", out var t)) translate = t.GetBoolean();
            if (root.TryGetProperty("target", out var g) && g.GetString() is "en" or "ja" or "es")
                Target = g.GetString()!;
            if (root.TryGetProperty("noticeShown", out var n)) TranslateNoticeShown = n.GetBoolean();
        }
        catch { /* missing or unreadable: defaults */ }

        // First run: the installer records the checkbox here.
        Translate = translate ?? InstallerChoseTranslation();
    }

    private static bool InstallerChoseTranslation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Rosetta");
            return key?.GetValue("Translation") is int v && v == 1;
        }
        catch { return false; }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new
            {
                enabled = Enabled,
                translate = Translate,
                target = Target,
                noticeShown = TranslateNoticeShown,
            }));
        }
        catch (Exception ex) { Log.Write($"settings save failed: {ex.Message}"); }
    }

    public static bool GetAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    public static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
    }
}
