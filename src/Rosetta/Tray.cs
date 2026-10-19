using System.Drawing;
using System.Windows.Forms;

namespace Rosetta;

/// <summary>
/// Notification-area icon: on/off toggle, start-with-Windows, exit. Windows files new tray
/// icons under "Show hidden icons" by default; the user can drag it onto the taskbar.
/// </summary>
internal sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabled;
    private readonly Icon _appIcon;
    private readonly Action<bool> _onToggle;

    public Tray(bool enabled, Action<bool> onToggle, Action onExit)
    {
        _onToggle = onToggle;
        using var stream = typeof(Tray).Assembly.GetManifestResourceStream("rosetta.ico")!;
        _appIcon = new Icon(stream);

        _enabled = new ToolStripMenuItem("Enabled") { Checked = enabled, CheckOnClick = true };
        _enabled.CheckedChanged += (_, _) => Apply(_enabled.Checked);

        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Settings.GetAutostart(),
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Settings.SetAutostart(autostart.Checked);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => onExit();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_enabled, autostart, new ToolStripSeparator(), exit]);

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _enabled.Checked = !_enabled.Checked;
        UpdateText(enabled);
    }

    private void Apply(bool on)
    {
        UpdateText(on);
        _onToggle(on);
    }

    private void UpdateText(bool on) => _icon.Text = on ? "Rosetta - on" : "Rosetta - off";

    public void Dispose()
    {
        _icon.Visible = false;   // otherwise the icon lingers until the mouse passes over it
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
