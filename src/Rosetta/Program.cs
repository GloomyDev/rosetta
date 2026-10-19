using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rosetta;

internal static class Program
{
    private const int VK_SHIFT = 0x10;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    /// <summary>
    /// FR-02. Shift is held for two unrelated reasons: scanning, and typing a capital.
    /// Without this check, typing a capitalised sentence fires a lookup wherever the
    /// mouse happens to be parked.
    ///
    /// This MUST use GetAsyncKeyState, not GetKeyboardState. The latter reports the
    /// calling thread's input queue, and the scan thread has no message queue at all,
    /// so it always came back empty and this check silently did nothing - the debug
    /// trace caught lookups firing on text in the terminal while the user typed.
    /// </summary>
    private static bool IsTyping()
    {
        foreach (int vk in TypingKeys)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    /// <summary>
    /// Keys that mean "the user is writing", deliberately excluding every modifier and
    /// mouse button. Scanning the full 0x08-0xFE range each tick would be wasteful;
    /// this is the set that actually produces text.
    /// </summary>
    private static readonly int[] TypingKeys = BuildTypingKeys();

    private static int[] BuildTypingKeys()
    {
        var keys = new List<int>();
        for (int vk = 0x30; vk <= 0x39; vk++) keys.Add(vk);   // 0-9
        for (int vk = 0x41; vk <= 0x5A; vk++) keys.Add(vk);   // A-Z
        for (int vk = 0x60; vk <= 0x69; vk++) keys.Add(vk);   // numpad 0-9
        for (int vk = 0xBA; vk <= 0xC0; vk++) keys.Add(vk);   // ;=,-./`
        for (int vk = 0xDB; vk <= 0xDE; vk++) keys.Add(vk);   // []\'
        keys.Add(0x20);                                        // space
        keys.Add(0x08);                                        // backspace
        keys.Add(0x0D);                                        // enter
        keys.Add(0x09);                                        // tab
        keys.Add(0x2E);                                        // delete
        return [.. keys];
    }

    // FR-03. 20ms matches the user's own tuned Yomitan config rather than the 50ms the
    // requirements originally guessed at.
    private const int RestDelayMs = 20;

    // How long after the last keystroke scanning stays suppressed (FR-02).
    private const int TypingCooldownMs = 400;

    // How long Shift must be held ALONE before scanning arms, when the pointer has not
    // moved. Typing presses Shift and the letter almost together and releases quickly,
    // so Shift effectively never sits alone this long while writing.
    private const int ShiftDwellMs = 200;

    /// <summary>Toggled from the tray menu; the scan loop idles while false.</summary>
    internal static volatile bool Enabled = true;

    private static PopupForm _popup = null!;
    private static readonly Yomitan Api = new();

    private static CancellationTokenSource? _inflight;
    private static Rectangle _anchor;
    private static string _lastTerm = "";
    private static int _stickyProfile = -1;
    private static string _stickyProcess = "";
    private static Point _lastNestedProbe = Point.Empty;

    [STAThread]
    private static void Main(string[] args)
    {
        // One instance only: a second tray icon and a second scanner would double every popup.
        using var single = new Mutex(true, @"Local\Rosetta.SingleInstance", out bool first);
        if (!first) return;

        Log.Enabled = args.Contains("--debug");
        if (Log.Enabled) AttachConsole(-1);   // WinForms has no console of its own
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console attached */ }

        ApplicationConfiguration.Initialize();
        _popup = new PopupForm();

        // Force handle creation HERE, on the UI thread, before anything else can touch
        // the form. Otherwise the first cross-thread call from the scanner creates the
        // handle on that thread instead: the window then belongs to a thread with no
        // message loop, so it never paints and WebView2 never finishes initialising.
        // That is what produced an empty black rectangle.
        _ = _popup.Handle;
        _popup.WebMessage += OnWebMessage;
        Log.Write("popup handle created on UI thread");

        Enabled = Settings.LoadEnabled();
        using var tray = new Tray(
            Enabled,
            on => { Enabled = on; Settings.SaveEnabled(on); },
            Application.Exit);

        // NFR-05. UIA runs on its own STA thread so a hung target app can never block
        // the UI thread - the popup stays responsive even when Word stops answering.
        var scanner = new Thread(ScanLoop) { IsBackground = true, Name = "uia-scan" };
        scanner.SetApartmentState(ApartmentState.STA);
        scanner.Start();

        Application.Run();
    }

    private static void ScanLoop()
    {
        using var capture = new Capture();

        // Check the backend once at startup and say so plainly if it is wrong (FR-20).
        CheckBackend();

        Point last = default;
        var rest = System.Diagnostics.Stopwatch.StartNew();
        bool shownForCurrentWord = false;
        var nestedProbeIdle = System.Diagnostics.Stopwatch.StartNew();
        var typingCooldown = new System.Diagnostics.Stopwatch();

        // FR-02 arming state. Shift means two different things - "look this up" and
        // "type a capital" - and they are told apart by how Shift is held, not by
        // whether it is held.
        var shiftHeld = new System.Diagnostics.Stopwatch();
        bool shiftWasDown = false;
        bool typingDuringHold = false;
        Point shiftDownAt = default;

        while (true)
        {
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) { Hide(); Thread.Sleep(50); continue; }

            if (!Enabled)
            {
                if (_popup.Visible) Hide();
                Thread.Sleep(100);
                continue;
            }

            // Poll typing on EVERY iteration, before anything can short-circuit. The
            // previous version checked this only inside the Shift branch, so ordinary
            // typing - where Shift is released for all but the capital - was invisible
            // to it and the cooldown never restarted.
            bool typingNow = IsTyping();
            if (typingNow)
            {
                typingCooldown.Restart();

                // FR-14. Any keystroke dismisses. Starting to type is an unambiguous
                // signal that the user has moved on from reading, and leaving the popup
                // up means it sits over whatever they are now writing. Modifiers are
                // excluded by IsTyping(), so holding Shift to scan does not self-dismiss.
                if (_popup.Visible) Hide();
            }

            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

            if (shift && !shiftWasDown)
            {
                // Shift just went down: start a fresh hold.
                shiftHeld.Restart();
                typingDuringHold = typingNow;
                GetCursorPos(out shiftDownAt);
            }
            else if (!shift && shiftWasDown)
            {
                shiftHeld.Reset();
                typingDuringHold = false;
            }
            else if (shift && typingNow)
            {
                // A keystroke anywhere in this hold disqualifies it, and stays
                // disqualifying until Shift is released - otherwise a long capitalised
                // run would satisfy the dwell partway through.
                typingDuringHold = true;
            }
            shiftWasDown = shift;

            if (!shift)
            {
                // FR-14: a click ELSEWHERE dismisses. A click inside the popup must not -
                // that is how the Back button is operated, and hiding here would both
                // close the popup and discard the nested history before the page's
                // message reached the host.
                if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 &&
                    GetCursorPos(out var click) && !_popup.ContainsScreenPoint(click))
                {
                    Hide();
                }
                Thread.Sleep(15);
                continue;
            }

            if (!GetCursorPos(out var pt)) { Thread.Sleep(15); continue; }

            // FR-14/FR-15: the cursor is over the popup. Keep it open, and drive a
            // nested lookup from here - the page cannot see Shift for itself.
            if (_popup.ContainsScreenPoint(pt))
            {
                if (pt != _lastNestedProbe)
                {
                    _lastNestedProbe = pt;
                    _popup.BeginInvoke(() => _popup.ProbeAt(pt));
                }
                else if (nestedProbeIdle.ElapsedMilliseconds > 150)
                {
                    // Shift may have been pressed with the cursor already stationary,
                    // so re-offer the same point occasionally. The page dedupes by term,
                    // so this cannot cause repeated lookups of the same word.
                    nestedProbeIdle.Restart();
                    _popup.BeginInvoke(() => _popup.ProbeAt(pt));
                }
                Thread.Sleep(25);
                continue;
            }
            _lastNestedProbe = Point.Empty;

            // FR-02. Scanning arms only when this Shift hold looks like an intent to
            // read rather than to write:
            //   - no keystroke at any point during the hold, and the cooldown elapsed
            //   - AND either Shift has been down alone for the dwell, OR the pointer has
            //     moved since Shift went down
            // Movement counts because the mouse does not move while typing, so the
            // common flow (hold Shift, sweep across text) stays instant and the dwell is
            // paid only when pressing Shift with the pointer already parked.
            if (typingDuringHold ||
                (typingCooldown.IsRunning && typingCooldown.ElapsedMilliseconds < TypingCooldownMs))
            {
                Thread.Sleep(15);
                continue;
            }

            bool movedSinceShift = pt != shiftDownAt;
            if (!movedSinceShift && shiftHeld.ElapsedMilliseconds < ShiftDwellMs)
            {
                Thread.Sleep(10);
                continue;
            }

            if (pt == last)
            {
                if (!shownForCurrentWord && rest.ElapsedMilliseconds >= RestDelayMs)
                {
                    shownForCurrentWord = true;
                    Probe(capture, pt);
                }
                Thread.Sleep(10);
                continue;
            }

            last = pt;
            rest.Restart();
            shownForCurrentWord = false;

            // Cursor left the matched word entirely - drop the popup (FR-14).
            if (!_anchor.IsEmpty && !Inflated(_anchor).Contains(pt)) Hide();

            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// FR-11. GetBoundingRectangles can return several rectangles, and next to an
    /// inline image the first one may be the image's box, empty, or implausibly large.
    /// Taking Rects[0] unchecked is what sent the popup to the screen edge: ShowFor
    /// then flipped against a bogus anchor. Prefer the rectangle the cursor is actually
    /// in, reject nonsense, and fall back to a caret-sized box at the cursor.
    /// </summary>
    private static Rectangle PickAnchor(Rectangle[] rects, Point cursor)
    {
        var screen = Screen.FromPoint(cursor).Bounds;
        Rectangle best = Rectangle.Empty;
        long bestDistance = long.MaxValue;

        foreach (var r in rects)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;      // empty
            if (r.Height > 120) continue;                      // taller than ~3 lines: not a word
            if (!screen.IntersectsWith(r)) continue;           // not on the cursor's monitor

            if (r.Contains(cursor)) return r;

            long dx = cursor.X < r.Left ? r.Left - cursor.X : cursor.X > r.Right ? cursor.X - r.Right : 0;
            long dy = cursor.Y < r.Top ? r.Top - cursor.Y : cursor.Y > r.Bottom ? cursor.Y - r.Bottom : 0;
            long d = dx * dx + dy * dy;
            if (d < bestDistance) { bestDistance = d; best = r; }
        }

        // Anything further than ~200px from the cursor is not the word being pointed at.
        if (best.IsEmpty || bestDistance > 200 * 200)
            return new Rectangle(cursor.X, cursor.Y, 1, 18);

        return best;
    }

    private static Rectangle Inflated(Rectangle r)
    {
        var x = r;
        x.Inflate(12, 10);
        return x;
    }

    private static void CheckBackend()
    {
        var ct = CancellationToken.None;
        if (!Api.IsAliveAsync(ct).GetAwaiter().GetResult())
        {
            Show(Html.Message("Yomitan API is not responding.",
                "Start the browser, enable Settings > General > Enable Yomitan API, then restart the browser."),
                new Rectangle(40, 40, 1, 1), new Point(40, 40));
            return;
        }
        if (!Api.IsPatchedAsync(ct).GetAwaiter().GetResult())
        {
            Show(Html.Message("Yomitan is running, but the profileIndex patch is not live.",
                "Language selection will not work. Reload the unpacked build from tools/yomitan-patched."),
                new Rectangle(40, 40, 1, 1), new Point(40, 40));
        }
    }

    private static void Probe(Capture capture, Point pt)
    {
        Captured? cap;
        try { cap = capture.At(pt); }
        catch (Exception ex) { Log.Write($"capture threw {ex.GetType().Name}: {ex.Message}"); return; }

        if (cap is null || cap.Term.Length == 0) { Log.Write("no text under cursor"); return; }
        Log.Write($"[{cap.Process}] span=\"{Clip(cap.Span)}\" term=\"{Clip(cap.Term)}\" rects={cap.Rects.Length}");

        // Don't re-query the word already on screen.
        if (cap.Term == _lastTerm && _popup.Visible) return;

        // NFR-10. The API host serializes requests, so a queue would compound latency.
        // A newer hover cancels the one in flight instead.
        var previous = _inflight;
        previous?.Cancel();
        previous?.Dispose();          // each CTS holds a timer registration; leaked otherwise
        var cts = new CancellationTokenSource();
        _inflight = cts;

        _ = Task.Run(() => LookupAndShowAsync(cap, pt, cts.Token), cts.Token);
    }

    private static async Task LookupAndShowAsync(Captured cap, Point pt, CancellationToken ct)
    {
        try
        {
            var anchor = PickAnchor(cap.Rects, pt);

            if (IsPepe(cap.Term))
            {
                Log.Write("  easter egg");
                Show(Html.Pepe(), anchor, pt);
                _lastTerm = cap.Term;
                return;
            }

            Lookup? hit;
            if (Lang.IsJapanese(cap.Span))
            {
                hit = await Api.LookupAsync(cap.Span, Lang.Japanese, ct);
            }
            else
            {
                if (cap.Term.Length < 2) return;   // single letters match everywhere, slowly
                hit = await ResolveLatinAsync(cap.Term, cap.Span, ct);
            }

            if (ct.IsCancellationRequested) return;
            if (hit is null)
            {
                // FR-20. Leaving the previous entry on screen makes a failed lookup look
                // like a WRONG lookup: hovering "bag" with no match still showed "this"
                // from the hover before it, which reads as the tool grabbing the wrong
                // word. Say there is no match instead.
                Log.Write($"  no entry for \"{Clip(cap.Term)}\"");
                Show(Html.Message($"No entry for “{cap.Term}”", LangNameOf(_stickyProfile)), anchor, pt);
                _lastTerm = cap.Term;
                return;
            }
            Log.Write($"  -> {hit.Term} ({hit.Reading}) profile={hit.ProfileIndex} match={hit.MatchedLength} anchor={anchor}");

            _lastTerm = cap.Term;
            _stickyProfile = hit.ProfileIndex;
            _stickyProcess = cap.Process;

            // First paint on the ~25ms path.
            Show(Html.FirstPaint(hit), anchor, pt);

            // Second paint: Yomitan's own rendered glossary, swapped in when it lands.
            // The rich paint MUST use the same profile the first paint resolved with.
            // /ankiFields has the same hardcoded-profile bug termEntries had, so without
            // passing it here an English word gets re-rendered through the Japanese
            // profile and the correct definition is replaced by romaji nonsense.
            var (glossary, furigana) = await Api.RichAsync(
                Lang.IsJapanese(cap.Span) ? cap.Span : cap.Term, hit.ProfileIndex, ct);

            if (ct.IsCancellationRequested || glossary is null) return;
            if (_lastTerm != cap.Term) return;     // the user moved on while we rendered

            // A nested lookup may have started while /ankiFields was in flight. Painting
            // the top-level entry now would silently throw away the chain the user is in.
            if (History.Count != 0) return;

            Log.Write("  rich paint");
            Show(Html.RichPaint(hit, glossary, furigana), anchor, pt);
        }
        catch (OperationCanceledException) { /* superseded by a newer hover */ }
        catch (Exception ex)
        {
            // An unobserved exception here would silently stop lookups working with no
            // visible symptom at all, which is exactly how this went wrong before.
            Log.Write($"  lookup FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Exact match on the whole term, never a substring. Normalising first (NFKC) folds
    /// full-width and half-width forms; for Latin text, diacritics are then stripped so
    /// "Pepe", "PEPE", "ｐｅｐｅ", "pépé" and "pepé" all match, and for Japanese
    /// ぺぺ (hiragana) and ペペ (katakana) both match. The diacritic strip is NOT applied
    /// to Japanese, because it would reduce ぺ to へ.
    /// </summary>
    private static bool IsPepe(string term)
    {
        string t = term.Trim().Normalize(System.Text.NormalizationForm.FormKC);
        if (t == "ぺぺ" || t == "ペペ") return true;

        var sb = new System.Text.StringBuilder(t.Length);
        foreach (char c in t.Normalize(System.Text.NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Equals("pepe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? LangNameOf(int profile) => profile switch
    {
        Lang.Japanese => "Japanese",
        Lang.English => "English",
        Lang.Spanish => "Spanish",
        _ => null,
    };

    private static string Clip(string s) => s.Length <= 24 ? s : s[..24] + "...";

    // ------------------------------------------------------------- FR-15 nested lookup

    // Rendered pages behind the current one. Storing the HTML rather than the query
    // makes Back instant and, more importantly, exact: re-running the lookup could
    // return something different if the user has changed profiles meanwhile.
    private const int MaxNestDepth = 10;   // matches Yomitan's popupNestingMaxDepth
    private static readonly List<string> History = [];
    private static string _currentHtml = "";

    private static void OnWebMessage(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            string action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

            if (action == "back")
            {
                if (History.Count == 0) return;
                string html = History[^1];
                History.RemoveAt(History.Count - 1);
                _currentHtml = html;
                Log.Write($"back -> depth {History.Count}");
                ShowHtmlOnly(html);
                return;
            }

            if (action == "open")
            {
                // The URL is fixed here on purpose: the page is not trusted to choose it.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://www.joselopezchapa.com") { UseShellExecute = true });
                return;
            }

            if (action != "lookup") return;
            string term = root.TryGetProperty("term", out var t) ? t.GetString() ?? "" : "";
            string span = root.TryGetProperty("span", out var s) ? s.GetString() ?? "" : "";
            if (term.Length == 0) return;

            Log.Write($"nested lookup \"{Clip(term)}\"");
            var prev = _inflight;
            prev?.Cancel();
            prev?.Dispose();
            var cts = new CancellationTokenSource();
            _inflight = cts;
            _ = Task.Run(() => NestedAsync(term, span, cts.Token), cts.Token);
        }
        catch (Exception ex)
        {
            Log.Write($"web message parse failed: {ex.Message}");
        }
    }

    private static async Task NestedAsync(string term, string span, CancellationToken ct)
    {
        try
        {
            term = Capture.Sanitize(term);
            if (term.Length == 0) return;

            Lookup? hit = Lang.IsJapanese(term)
                ? await Api.LookupAsync(term, Lang.Japanese, ct)
                : term.Length < 2 ? null : await ResolveLatinAsync(term, span, ct);

            if (ct.IsCancellationRequested || hit is null) { Log.Write($"  nested: no entry"); return; }

            // Push the page we are leaving, then paint the new one one level deeper.
            if (History.Count >= MaxNestDepth) History.RemoveAt(0);
            History.Add(_currentHtml);
            int depth = History.Count;
            Log.Write($"  nested -> {hit.Term} (depth {depth})");

            ShowHtmlOnly(Html.FirstPaint(hit, depth));

            var (glossary, furigana) = await Api.RichAsync(term, hit.ProfileIndex, ct);
            if (ct.IsCancellationRequested || glossary is null) return;
            if (History.Count != depth) return;   // the user navigated while we rendered

            ShowHtmlOnly(Html.RichPaint(hit, glossary, furigana, depth));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"  nested FAILED {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Repaint without moving the window. A nested lookup must not reposition the
    /// popup - the anchor rectangle it would use is inside the popup itself, so the
    /// window would walk across the screen with every nested hover.
    /// </summary>
    private static void ShowHtmlOnly(string html)
    {
        if (_popup.InvokeRequired) { _popup.BeginInvoke(() => ShowHtmlOnly(html)); return; }
        _currentHtml = html;
        _popup.SetHtml(html);
    }

    /// <summary>
    /// FR-10. English and Spanish share the Latin script, so they are separated by
    /// lookup: query BOTH and keep the better match. Profile 0 is excluded on purpose -
    /// with wanakana on it romaji-converts Latin input and answers confidently wrong
    /// ("running" resolves to a Japanese word), which would poison the ranking.
    /// </summary>
    private static async Task<Lookup?> ResolveLatinAsync(string term, string span, CancellationToken ct)
    {
        var en = await Api.LookupAsync(term, Lang.English, ct);
        if (ct.IsCancellationRequested) return null;
        var es = await Api.LookupAsync(term, Lang.Spanish, ct);

        if (en is null && es is null) return null;
        if (en is null) return es;
        if (es is null) return en;

        if (es.MatchedLength != en.MatchedLength)
            return es.MatchedLength > en.MatchedLength ? es : en;

        int bias = Lang.SpanishBias(span);
        if (bias != 0) return bias > 0 ? es : en;

        // Still tied: stay in whatever language this app was last reading, so the popup
        // does not flicker between languages inside one document.
        if (_stickyProfile == Lang.Spanish) return es;
        return en;
    }

    // Every UI touch marshals. The handle is guaranteed to exist by now (Main forces
    // it), so InvokeRequired alone is the right test - guarding on IsHandleCreated as
    // well would silently let a background thread through before the handle existed.
    private static void Show(string html, Rectangle anchor, Point cursor)
    {
        if (_popup.InvokeRequired)
        {
            _popup.BeginInvoke(() => Show(html, anchor, cursor));
            return;
        }
        // A top-level hover starts a fresh chain, so any nested history is abandoned.
        History.Clear();
        _currentHtml = html;
        _anchor = anchor;
        _popup.SetHtml(html);
        _popup.ShowFor(anchor, cursor);
    }

    private static void Hide()
    {
        if (_popup.InvokeRequired)
        {
            _popup.BeginInvoke(Hide);
            return;
        }
        _anchor = Rectangle.Empty;
        _lastTerm = "";
        History.Clear();          // each entry holds ~8KB of rendered HTML
        _currentHtml = "";
        _popup.HidePopup();
    }
}
