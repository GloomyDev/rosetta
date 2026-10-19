using System.Text.Json;
using Microsoft.Win32;

namespace Rosetta;

/// <summary>Per-user state: the on/off toggle and the start-with-Windows registration.</summary>
internal static class Settings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rosetta");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Rosetta";

    public static bool LoadEnabled()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            return !doc.RootElement.TryGetProperty("enabled", out var e) || e.GetBoolean();
        }
        catch { return true; }   // missing or unreadable: default to on
    }

    public static void SaveEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { enabled }));
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
