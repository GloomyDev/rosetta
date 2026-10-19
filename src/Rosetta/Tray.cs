using System.Drawing;
using System.Windows.Forms;

namespace Rosetta;

/// <summary>
/// Notification-area icon: on/off toggle, online translation, start-with-Windows, exit.
/// Windows files new tray icons under "Show hidden icons" by default; the user can drag
/// it onto the taskbar.
/// </summary>
internal sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabled;
    private readonly Icon _appIcon;

    public Tray(Action onExit)
    {
        using var stream = typeof(Tray).Assembly.GetManifestResourceStream("rosetta.ico")!;
        _appIcon = new Icon(stream);

        _enabled = new ToolStripMenuItem("Enabled") { Checked = Settings.Enabled, CheckOnClick = true };
        _enabled.CheckedChanged += (_, _) =>
        {
            Settings.Enabled = _enabled.Checked;
            Settings.Save();
            UpdateText();
        };

        var translate = new ToolStripMenuItem("Translate selection (online)")
        {
            Checked = Settings.Translate,
            CheckOnClick = true,
            ToolTipText = "Hold Ctrl over selected text to translate it. Sends the text to Google Translate. Not local.",
        };
        translate.CheckedChanged += (_, _) =>
        {
            Settings.Translate = translate.Checked;
            if (translate.Checked && !Settings.TranslateNoticeShown)
            {
                Settings.TranslateNoticeShown = true;
                _icon.ShowBalloonTip(8000, "Online translation is on",
                    "Hold Ctrl over selected text to translate it. The text is sent to Google Translate. Translation is not local.",
                    ToolTipIcon.Info);
            }
            Settings.Save();
        };

        var target = new ToolStripMenuItem("Translate to");
        foreach (var (code, name) in new[] { ("en", "English"), ("ja", "Japanese"), ("es", "Spanish") })
        {
            var item = new ToolStripMenuItem(name) { Checked = Settings.Target == code };
            item.Click += (_, _) =>
            {
                Settings.Target = code;
                Settings.Save();
                foreach (ToolStripMenuItem other in target.DropDownItems)
                    other.Checked = ReferenceEquals(other, item);
            };
            target.DropDownItems.Add(item);
        }

        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Settings.GetAutostart(),
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Settings.SetAutostart(autostart.Checked);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => onExit();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_enabled, new ToolStripSeparator(), translate, target,
                             new ToolStripSeparator(), autostart, exit]);

        _icon = new NotifyIcon { Icon = _appIcon, ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => _enabled.Checked = !_enabled.Checked;
        UpdateText();
    }

    private void UpdateText() => _icon.Text = Settings.Enabled ? "Rosetta - on" : "Rosetta - off";

    public void Dispose()
    {
        _icon.Visible = false;   // otherwise the icon lingers until the mouse passes over it
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
