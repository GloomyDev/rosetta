namespace Rosetta;

/// <summary>
/// Console tracing. The popup is invisible when it goes wrong, so there has to be
/// somewhere to look - a silent failure in a hover tool is otherwise undebuggable.
/// Enabled with --debug.
/// </summary>
internal static class Log
{
    public static bool Enabled;

    public static void Write(string msg)
    {
        if (!Enabled) return;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }
}
