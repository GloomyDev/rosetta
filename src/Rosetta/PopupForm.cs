using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Rosetta;

/// <summary>
/// FR-11 / FR-12 / NFR-03. A WebView2 surface that shows dictionary entries over
/// whatever app the user is reading, and never takes keyboard focus from it.
///
/// NFR-03 is the requirement that makes this awkward: typing in the Teams compose box
/// while the popup is open must not lose a keystroke. That needs all three of
/// WS_EX_NOACTIVATE, ShowWithoutActivation, and showing via SW_SHOWNOACTIVATE - any one
/// alone still lets the window take activation in some paths.
/// </summary>
internal sealed class PopupForm : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST    = 0x00000008;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
    private bool _ready;
    private string? _pending;

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Size PreferredPopupSize { get; set; } = new(420, 260);

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = PreferredPopupSize;
        BackColor = Color.FromArgb(32, 36, 48);
        Padding = new Padding(1);
        Controls.Add(_web);
    }

    /// <summary>
    /// WebView2 initialisation must start on the thread that owns the window handle and
    /// pumps its message loop. Kicking it off from the constructor meant the await
    /// continuation had no SynchronizationContext to resume on when the handle happened
    /// to be created from the scanner thread, so _ready never became true and the popup
    /// showed as an empty dark rectangle.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_initStarted)
        {
            _initStarted = true;
            _ = InitAsync();
        }
    }

    private bool _initStarted;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Path.GetTempPath(), "rosetta-webview2"));
            await _web.EnsureCoreWebView2Async(env);

            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;

            // The popup is a display surface: it must never navigate anywhere.
            _web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            _web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                    !e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                }
            };

            // FR-15: the page posts nested-lookup and back requests here.
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try { WebMessage?.Invoke(e.TryGetWebMessageAsString() ?? ""); }
                catch (Exception ex) { Log.Write($"web message failed: {ex.Message}"); }
            };

            _ready = true;
            Log.Write("webview2 ready");
            if (_pending is not null) { _web.NavigateToString(_pending); _pending = null; }
        }
        catch (Exception ex)
        {
            // Without the WebView2 runtime there is no popup at all - say so loudly
            // rather than failing silently (FR-20).
            Log.Write($"WebView2 init FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Raised with the raw JSON a nested lookup or Back click posted.</summary>
    public event Action<string>? WebMessage;

    /// <summary>
    /// Ask the page to look up whatever is at this screen point. The page cannot see
    /// the Shift key itself - a WS_EX_NOACTIVATE window never receives keyboard input -
    /// so the host, which polls Shift globally anyway, has to drive this. Without it a
    /// nested lookup only fires on mouse MOVEMENT, and holding Shift over an already
    /// stationary cursor does nothing.
    /// </summary>
    public void ProbeAt(Point screen)
    {
        if (!_ready) return;
        try
        {
            var client = _web.PointToClient(screen);
            // Client coordinates are device pixels; the page works in CSS pixels.
            double scale = DeviceDpi / 96.0;
            int x = (int)Math.Round(client.X / scale);
            int y = (int)Math.Round(client.Y / scale);
            _web.CoreWebView2.PostWebMessageAsJson(
                $"{{\"action\":\"probe\",\"x\":{x},\"y\":{y}}}");
        }
        catch (Exception ex) { Log.Write($"ProbeAt failed: {ex.Message}"); }
    }

    public void SetHtml(string html)
    {
        if (!_ready) { _pending = html; return; }
        if (_suspended)
        {
            // Resume is implicit on the next navigation, but calling it explicitly keeps
            // the first paint after a hide from stalling behind renderer startup.
            try { _web.CoreWebView2.Resume(); } catch { /* not suspended after all */ }
            _suspended = false;
        }
        _web.NavigateToString(html);
    }

    /// <summary>
    /// FR-11. Place the popup against the matched text, keeping it fully on screen and
    /// flipping above or left near edges. Anchor is in physical screen pixels, which is
    /// what UIA bounding rectangles are given in (hence PerMonitorV2 in app.manifest).
    /// </summary>
    public void ShowFor(Rectangle anchor, Point cursor)
    {
        var area = Screen.FromPoint(cursor.IsEmpty ? new Point(anchor.X, anchor.Y) : cursor).WorkingArea;
        var size = PreferredPopupSize;

        if (anchor.Width <= 0 || anchor.Height <= 0)
            anchor = new Rectangle(cursor.X, cursor.Y, 1, 18);

        const int gap = 6;
        int x = anchor.Left;
        int y = anchor.Bottom + gap;

        if (y + size.Height > area.Bottom) y = anchor.Top - gap - size.Height;  // flip above
        if (y < area.Top) y = Math.Min(anchor.Bottom + gap, area.Bottom - size.Height);

        if (x + size.Width > area.Right) x = area.Right - size.Width;           // flip left
        if (x < area.Left) x = area.Left;

        Bounds = new Rectangle(x, y, size.Width, size.Height);
        ShowNoActivate();
    }

    private void ShowNoActivate()
    {
        if (!Visible)
        {
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            Visible = true;
        }
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOMOVE);
    }

    /// <summary>
    /// NFR-08. Hiding the window leaves the WebView2 renderer process fully resident -
    /// tens of MB that never came back after the popup closed. Navigating away releases
    /// the document, and TrySuspend lets WebView2 release the renderer itself. Both are
    /// undone lazily on the next show.
    /// </summary>
    public void HidePopup()
    {
        if (!Visible) return;
        Visible = false;

        if (!_ready) return;
        try
        {
            _web.CoreWebView2.Navigate("about:blank");
            // Suspend only once the blank page has settled; suspending mid-navigation
            // fails and would leave the renderer resident anyway.
            BeginInvoke(() =>
            {
                try { _ = _web.CoreWebView2.TrySuspendAsync(); }
                catch (Exception ex) { Log.Write($"suspend failed: {ex.Message}"); }
            });
            _suspended = true;
        }
        catch (Exception ex) { Log.Write($"hide cleanup failed: {ex.Message}"); }
    }

    private bool _suspended;

    public bool ContainsScreenPoint(Point p) => Visible && Bounds.Contains(p);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _web.Dispose();
        base.Dispose(disposing);
    }
}
