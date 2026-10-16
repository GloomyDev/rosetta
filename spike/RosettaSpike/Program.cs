using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace RosettaSpike;

/// <summary>
/// Feasibility spike for the system-wide pop-up dictionary (requirements.md, Step 3).
/// Answers, with measurements rather than assumptions:
///   1. Is the patched Yomitan still live? (profileIndex must be rejected when invalid)
///   2. Does UIA expose TextPattern on Word / Teams / a PDF viewer?
///   3. What does the full chain actually cost, stage by stage? (NFR-01)
/// </summary>
internal static class Program
{
    private const string ApiBase = "http://127.0.0.1:19633";

    // FR-04 defaults. Note these differ from the doc (50ms / 20 chars): the user's own
    // Yomitan config uses 20ms / 16 chars, which is tuned by actual use.
    private const int ScanLength = 16;

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(ApiBase),
        // NFR-10: the API host serializes requests, so a generous timeout here would
        // compound latency rather than add resilience.
        Timeout = TimeSpan.FromMilliseconds(3000),
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        try
        {
            switch (mode)
            {
                case "api":
                    return CheckApi().GetAwaiter().GetResult() ? 0 : 1;
                case "hover":
                    HoverLoop();
                    return 0;
                case "diag":
                    DiagLoop();
                    return 0;
                case "all":
                default:
                    if (!CheckApi().GetAwaiter().GetResult())
                    {
                        Console.WriteLine("\nAPI checks failed - fix those before trusting any hover result.");
                        return 1;
                    }
                    HoverLoop();
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nFATAL: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    // ---------------------------------------------------------------- API checks

    private static async Task<bool> CheckApi()
    {
        Header("1. Yomitan API");

        string? version = await TryPostAsync("yomitanVersion", new { });
        if (version is null)
        {
            Console.WriteLine("  FAIL  no response on " + ApiBase);
            Console.WriteLine("        -> is the browser running, and is 'Enable Yomitan API' on?");
            Console.WriteLine("        -> Edge only reads native-host registrations at startup; restart it.");
            return false;
        }
        Console.WriteLine($"  ok    server reachable, {Trim(version, 60)}");

        // Establish a baseline first: a VALID request must succeed. Without this, the
        // invalid-index check below passes trivially whenever every request is failing,
        // which is exactly the false positive that hid a chunked-encoding bug here.
        if (await TryPostAsync("termEntries", new { term = "test", profileIndex = 0 }) is null)
        {
            Console.WriteLine("  FAIL  a valid termEntries request failed - the API is reachable but not answering.");
            Console.WriteLine("        Check Content-Length handling and that a profile 0 exists.");
            return false;
        }
        Console.WriteLine("  ok    valid lookup succeeds (baseline)");

        // The side-loaded patched build can be silently replaced by the store version,
        // which would make profileIndex a no-op again and quietly break language
        // selection. An invalid index MUST be rejected.
        bool rejected = await TryPostAsync("termEntries", new { term = "test", profileIndex = 99 }) is null;
        if (!rejected)
        {
            Console.WriteLine("  FAIL  profileIndex=99 was accepted -> the PATCH IS NOT LIVE.");
            Console.WriteLine("        Edge has probably reverted to the store build of Yomitan.");
            Console.WriteLine("        Reload the unpacked extension from tools/yomitan-patched.");
            return false;
        }
        Console.WriteLine("  ok    patch live (invalid profileIndex rejected)");

        Header("2. Per-profile language resolution");
        var probes = new (string Term, int Profile, string Expect)[]
        {
            ("食べられなかった", 0, "食べる"),
            ("running",   1, "run"),
            ("corriendo", 2, "correr"),
        };
        bool allOk = true;
        foreach (var (term, profile, expect) in probes)
        {
            var (head, _, _) = await LookupAsync(term, profile);
            string status = head is null ? "FAIL " : "ok   ";
            if (head is null) allOk = false;
            Console.WriteLine($"  {status} [{profile}] {term,-16} -> {head ?? "NO ENTRIES"}   (expected near '{expect}')");
        }

        Header("3. Lookup latency (NFR-01 budget: 150ms p95 total, Word)");
        await BenchAsync("termEntries", () => TryPostAsync("termEntries",
            new { term = "食べられなかった", profileIndex = 0 }));
        await BenchAsync("ankiFields(max 1)", () => TryPostAsync("ankiFields", new
        {
            text = "食べられなかった",
            type = "term",
            markers = new[] { "expression", "reading", "glossary", "pitch-accents", "frequencies" },
            maxEntries = 1,
            includeMedia = false,
        }));

        return allOk;
    }

    private static async Task<(string? Head, int MatchedLength, long Ms)> LookupAsync(string term, int profileIndex)
    {
        var sw = Stopwatch.StartNew();
        string? json = await TryPostAsync("termEntries", new { term, profileIndex });
        sw.Stop();
        if (json is null) return (null, 0, sw.ElapsedMilliseconds);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("dictionaryEntries", out var entries) ||
            entries.GetArrayLength() == 0)
        {
            return (null, 0, sw.ElapsedMilliseconds);
        }

        var first = entries[0];
        int matched = first.TryGetProperty("maxOriginalTextLength", out var m) ? m.GetInt32() : 0;
        string? head = null;
        if (first.TryGetProperty("headwords", out var hw) && hw.GetArrayLength() > 0)
        {
            var h = hw[0];
            string t = h.TryGetProperty("term", out var tv) ? tv.GetString() ?? "" : "";
            string r = h.TryGetProperty("reading", out var rv) ? rv.GetString() ?? "" : "";
            head = string.IsNullOrEmpty(r) || r == t ? t : $"{t} ({r})";
        }
        return (head, matched, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// The API host is Python's single-threaded http.server, which reads the request
    /// body strictly by Content-Length. JsonContent / PostAsJsonAsync streams without
    /// one (chunked), the host then reads an empty body and answers 500 "{}".
    /// So the payload is always serialized up front into a StringContent.
    /// </summary>
    private static async Task<string?> TryPostAsync(string path, object body)
    {
        try
        {
            string payload = JsonSerializer.Serialize(body);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync("/" + path, content);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    private static async Task BenchAsync(string label, Func<Task<string?>> call)
    {
        await call(); // warm
        var ts = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            await call();
            sw.Stop();
            ts.Add(sw.Elapsed.TotalMilliseconds);
        }
        ts.Sort();
        Console.WriteLine($"  {label,-20} p50={ts[ts.Count / 2],6:F1}ms  p95={ts[(int)(0.95 * ts.Count) - 1],6:F1}ms");
    }

    // ---------------------------------------------------------------- UIA hover

    private static void HoverLoop()
    {
        Header("4. UIA capture - hold SHIFT and hover text; ESC to quit");
        Console.WriteLine("  Open Word, Teams or a PDF, hold Shift and point at a word.");
        Console.WriteLine();

        using var automation = new UIA3Automation();
        var seenProcesses = new Dictionary<string, bool>();
        Point last = default;
        var sinceLast = Stopwatch.StartNew();

        while (true)
        {
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) break;

            bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            if (!shift) { Thread.Sleep(15); continue; }

            if (!GetCursorPos(out var pt)) { Thread.Sleep(15); continue; }
            if (pt.X == last.X && pt.Y == last.Y) { Thread.Sleep(15); continue; }
            if (sinceLast.ElapsedMilliseconds < 120) { Thread.Sleep(10); continue; }

            last = pt;
            sinceLast.Restart();
            Probe(automation, pt, seenProcesses);
        }

        Header("Summary: TextPattern support by process");
        foreach (var (proc, ok) in seenProcesses)
            Console.WriteLine($"  {(ok ? "ok   " : "FAIL ")} {proc,-20} {(ok ? "TextPattern available" : "NO TextPattern - needs the Name fallback (FR-05)")}");
    }

    // ------------------------------------------------- Part B: can UIA give exact web text?

    /// <summary>
    /// Answers one question: on real web pages, can we get a text range that resolves at
    /// the cursor AND returns bounding rectangles? If yes, Rosetta's estimation paths can
    /// be deleted. If no - or if Firefox exposes nothing usable - a browser extension is
    /// the only way to match Yomitan's accuracy, which matters because ~90% of use is in
    /// the browser.
    /// </summary>
    private static void DiagLoop()
    {
        Header("UIA text-range diagnostic - hold SHIFT and hover; ESC to quit");
        Console.WriteLine("  Try: a heading, body paragraph text, a link, and styled/nested markup.");
        Console.WriteLine("  Run this in Edge, Chrome and Firefox and compare.");
        Console.WriteLine();

        using var automation = new UIA3Automation();
        var summary = new Dictionary<string, (int Hovers, int LeafTp, int AncestorTp, int DocTp, int WithRects)>();
        Point last = default;
        var sinceLast = Stopwatch.StartNew();

        while (true)
        {
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) break;
            if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0) { Thread.Sleep(15); continue; }
            if (!GetCursorPos(out var pt)) { Thread.Sleep(15); continue; }
            if (pt == last || sinceLast.ElapsedMilliseconds < 400) { Thread.Sleep(15); continue; }
            last = pt; sinceLast.Restart();

            Diagnose(automation, pt, summary);
        }

        Header("Summary by process");
        Console.WriteLine($"  {"process",-18} {"hovers",6} {"leaf TP",8} {"ancestor",9} {"Document",9} {"w/ rects",9}");
        foreach (var (proc, s) in summary)
            Console.WriteLine($"  {proc,-18} {s.Hovers,6} {s.LeafTp,8} {s.AncestorTp,9} {s.DocTp,9} {s.WithRects,9}");
        Console.WriteLine();
        Console.WriteLine("  'Document' > 0 with 'w/ rects' > 0 means UIA can replace estimation (Part C).");
        Console.WriteLine("  All zeros in a browser means that browser needs the extension (Part D).");
    }

    private static void Diagnose(UIA3Automation automation, Point pt,
        Dictionary<string, (int, int, int, int, int)> summary)
    {
        string proc = "?";
        int leafTp = 0, ancestorTp = 0, docTp = 0, withRects = 0;

        try
        {
            var swElem = Stopwatch.StartNew();
            var element = automation.FromPoint(pt);
            swElem.Stop();
            if (element is null) return;

            try { proc = Process.GetProcessById(element.Properties.ProcessId.Value).ProcessName; }
            catch { }

            string leafType = element.Properties.ControlType.ValueOrDefault.ToString();
            var leafPattern = element.Patterns.Text.PatternOrDefault;
            leafTp = leafPattern is not null ? 1 : 0;
            Console.WriteLine($"  [{proc}] leaf={leafType} textPattern={(leafTp == 1 ? "YES" : "no")}  ({swElem.ElapsedMilliseconds}ms)");

            // How far up is the nearest text pattern, and what kind of node is it?
            var swWalk = Stopwatch.StartNew();
            AutomationElement? firstWithText = null;
            int firstDepth = -1;
            AutomationElement? document = null;
            int documentDepth = -1;

            try
            {
                var walker = automation.TreeWalkerFactory.GetControlViewWalker();
                var cur = walker.GetParent(element);
                for (int depth = 1; depth <= 30 && cur is not null; depth++)
                {
                    bool hasText = cur.Patterns.Text.PatternOrDefault is not null;
                    var type = cur.Properties.ControlType.ValueOrDefault;

                    if (hasText && firstWithText is null) { firstWithText = cur; firstDepth = depth; }
                    if (type == ControlType.Document && hasText) { document = cur; documentDepth = depth; break; }
                    cur = walker.GetParent(cur);
                }
            }
            catch (Exception ex) { Console.WriteLine($"          walk failed: {ex.GetType().Name}"); }
            swWalk.Stop();

            if (firstWithText is not null)
            {
                ancestorTp = 1;
                Console.WriteLine($"          nearest textPattern: depth {firstDepth} " +
                                  $"({firstWithText.Properties.ControlType.ValueOrDefault})  ({swWalk.ElapsedMilliseconds}ms)");
                if (firstDepth > 5)
                    Console.WriteLine($"          -> BEYOND the app's 5-level cap: this is why it falls back to guessing");
            }
            else
            {
                Console.WriteLine($"          no textPattern within 30 ancestors  ({swWalk.ElapsedMilliseconds}ms)");
            }

            // The decisive test: a document-wide range resolved at the cursor.
            var target = document ?? firstWithText;
            int targetDepth = document is not null ? documentDepth : firstDepth;
            if (target is not null)
            {
                if (document is not null) docTp = 1;
                var tp = target.Patterns.Text.Pattern;
                var swRange = Stopwatch.StartNew();
                try
                {
                    var range = tp.RangeFromPoint(pt);
                    if (range is not null)
                    {
                        range.ExpandToEnclosingUnit(TextUnit.Character);
                        string ch = range.GetText(4) ?? "";
                        var charRects = range.GetBoundingRectangles();

                        var wordRange = range.Clone();
                        wordRange.ExpandToEnclosingUnit(TextUnit.Word);
                        string word = (wordRange.GetText(64) ?? "").Replace("\r", "").Replace("\n", "\\n");
                        var wordRects = wordRange.GetBoundingRectangles();
                        swRange.Stop();

                        if (charRects.Length > 0 || wordRects.Length > 0) withRects = 1;

                        bool contains = false;
                        foreach (var r in wordRects)
                            if (r.Contains(pt)) { contains = true; break; }

                        Console.WriteLine($"          range from {(document is not null ? "Document" : "ancestor")} " +
                                          $"depth {targetDepth}: char={Show(ch)} word=\"{Trim(word, 24)}\"");
                        Console.WriteLine($"          rects: char={charRects.Length} word={wordRects.Length} " +
                                          $"containsCursor={(contains ? "YES" : "no")}  ({swRange.ElapsedMilliseconds}ms)");
                    }
                    else
                    {
                        swRange.Stop();
                        Console.WriteLine($"          RangeFromPoint returned null  ({swRange.ElapsedMilliseconds}ms)");
                    }
                }
                catch (Exception ex)
                {
                    swRange.Stop();
                    Console.WriteLine($"          range failed: {ex.GetType().Name}: {Trim(ex.Message, 50)}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{proc}] diagnose failed: {ex.GetType().Name}: {Trim(ex.Message, 60)}");
        }

        var prev = summary.TryGetValue(proc, out var v) ? v : (0, 0, 0, 0, 0);
        summary[proc] = (prev.Item1 + 1, prev.Item2 + leafTp, prev.Item3 + ancestorTp,
                         prev.Item4 + docTp, prev.Item5 + withRects);
    }

    private static string Show(string s)
    {
        if (s.Length == 0) return "(empty)";
        char c = s[0];
        if (c == ' ') return "' ' (space)";
        if (c == '\r' || c == '\n') return "(newline)";
        return $"'{c}'";
    }

    private static void Probe(UIA3Automation automation, Point pt, Dictionary<string, bool> seen)
    {
        var total = Stopwatch.StartNew();
        string proc = "?";
        try
        {
            var swElem = Stopwatch.StartNew();
            var element = automation.FromPoint(pt);
            swElem.Stop();
            if (element is null) return;

            try { proc = Process.GetProcessById(element.Properties.ProcessId.Value).ProcessName; }
            catch { /* process may have gone away mid-probe */ }

            var swText = Stopwatch.StartNew();
            var textPattern = element.Patterns.Text.PatternOrDefault;
            string? captured = null;
            Rectangle[] rects = [];

            string? latinWord = null;
            if (textPattern is not null)
            {
                var range = textPattern.RangeFromPoint(pt);
                if (range is not null)
                {
                    range.ExpandToEnclosingUnit(TextUnit.Character);
                    // FR-04: extend forward, stopping at the end of the line.
                    range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, ScanLength);
                    captured = range.GetText(ScanLength + 8);
                    rects = range.GetBoundingRectangles();

                    // Forward-only scanning is correct for Japanese (it is what Yomitan
                    // does), but for Latin scripts it clips the word whenever the cursor
                    // lands mid-word: "construccion" captures as "onstruccion". Re-range
                    // to the enclosing WORD in that case.
                    if (captured is not null && !IsJapanese(captured))
                    {
                        var wordRange = textPattern.RangeFromPoint(pt);
                        if (wordRange is not null)
                        {
                            wordRange.ExpandToEnclosingUnit(TextUnit.Word);
                            latinWord = wordRange.GetText(64)?.Trim();
                            rects = wordRange.GetBoundingRectangles();
                        }
                    }
                }
            }
            swText.Stop();

            bool hasText = textPattern is not null;
            seen[proc] = seen.TryGetValue(proc, out bool prev) ? prev || hasText : hasText;

            if (captured is null)
            {
                // No TextPattern on the element under the cursor. For Chromium hosts
                // (Teams) the text often lives on an ancestor, so walk up before giving
                // up - and report enough detail to tell "tree not built" apart from
                // "tree built but this node has no text".
                string name = element.Properties.Name.ValueOrDefault ?? "";
                string ctrl = element.Properties.ControlType.ValueOrDefault.ToString();
                string fw = element.Properties.FrameworkId.ValueOrDefault ?? "?";
                string cls = element.Properties.ClassName.ValueOrDefault ?? "";

                string ancestor = "none";
                try
                {
                    var walker = automation.TreeWalkerFactory.GetControlViewWalker();
                    var cur = walker.GetParent(element);
                    for (int depth = 1; depth <= 5 && cur is not null; depth++)
                    {
                        if (cur.Patterns.Text.PatternOrDefault is not null)
                        {
                            var ar = cur.Patterns.Text.Pattern.RangeFromPoint(pt);
                            string? atext = null;
                            if (ar is not null)
                            {
                                ar.ExpandToEnclosingUnit(TextUnit.Character);
                                ar.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, ScanLength);
                                atext = ar.GetText(ScanLength + 8);
                            }
                            ancestor = $"depth {depth} ({cur.Properties.ControlType.ValueOrDefault}) -> \"{Trim(atext ?? "", 24)}\"";
                            break;
                        }
                        cur = walker.GetParent(cur);
                    }
                }
                catch (Exception ex) { ancestor = $"walk failed: {ex.GetType().Name}"; }

                Console.WriteLine($"  [{proc}] no TextPattern  type={ctrl} fw={fw} class={Trim(cls, 18)}");
                Console.WriteLine($"          Name=\"{Trim(name, 40)}\"");
                Console.WriteLine($"          ancestor with TextPattern: {ancestor}   elem={swElem.ElapsedMilliseconds}ms");
                return;
            }

            captured = Sanitize(captured);
            latinWord = latinWord is null ? null : Sanitize(latinWord);

            if (captured.Length == 0)
            {
                Console.WriteLine($"  [{proc}] span empty after sanitising (newline or embedded object only)");
                return;
            }

            var swLookup = Stopwatch.StartNew();

            string? head;
            int matched;
            string lang;
            if (IsJapanese(captured))
            {
                (head, matched, _) = LookupAsync(captured, 0).GetAwaiter().GetResult();
                lang = "ja";
            }
            else
            {
                string term = FirstWord(latinWord ?? captured);

                // A single Latin letter matches in both dictionaries and returns a huge
                // payload, twice - measured at 121ms on the lookup leg versus ~25ms for
                // a real word. Not worth querying.
                if (term.Length < 2)
                {
                    Console.WriteLine($"  [{proc}] \"{term}\" too short to look up (skipped)");
                    return;
                }

                var r = ResolveLatin(term, captured);
                head = r.Head;
                matched = r.Matched;
                lang = r.Profile switch { 1 => "en", 2 => "es", _ => "??" };
            }
            swLookup.Stop();
            total.Stop();

            string shown = latinWord ?? captured;
            Console.WriteLine($"  [{proc}] \"{Trim(shown, 28)}\"");
            Console.WriteLine($"          lang={lang} rects={rects.Length} match={matched} -> {head ?? "no entry"}");
            Console.WriteLine($"          elem={swElem.ElapsedMilliseconds}ms text={swText.ElapsedMilliseconds}ms " +
                              $"lookup={swLookup.ElapsedMilliseconds}ms TOTAL={total.ElapsedMilliseconds}ms" +
                              $"{(total.ElapsedMilliseconds > 150 ? "  <-- OVER NFR-01" : "")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{proc}] probe failed: {ex.GetType().Name}: {Trim(ex.Message, 60)}");
        }
    }

    /// <summary>FR-10 step 1: kana or Han anywhere in the span means Japanese.</summary>
    private static bool IsJapanese(string s)
    {
        foreach (char c in s)
        {
            if ((c >= 0x3040 && c <= 0x30FF) ||   // hiragana + katakana
                (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK unified ideographs
                (c >= 0x3400 && c <= 0x4DBF) ||   // CJK extension A
                (c >= 0xFF66 && c <= 0xFF9D))     // half-width katakana
            {
                return true;
            }
        }
        return false;
    }

    // ñ á é í ó ú ü ¿ ¡ - a strong Spanish signal when present, too sparse to rely on.
    private const string SpanishOnly = "ñáéíóúü¿¡";

    /// <summary>
    /// FR-10 step 2. English and Spanish share the Latin script, so they are separated
    /// by lookup rather than by inspection: query BOTH profiles and keep the better
    /// match. Profile 0 (Japanese) is deliberately excluded - it romaji-converts Latin
    /// input and returns confident nonsense rather than no result, so including it
    /// would poison the ranking.
    /// </summary>
    private static (string? Head, int Matched, int Profile) ResolveLatin(string term, string span)
    {
        var en = LookupAsync(term, 1).GetAwaiter().GetResult();
        var es = LookupAsync(term, 2).GetAwaiter().GetResult();

        if (en.Head is null && es.Head is null) return (null, 0, -1);
        if (en.Head is null) return (es.Head, es.MatchedLength, 2);
        if (es.Head is null) return (en.Head, en.MatchedLength, 1);

        if (es.MatchedLength != en.MatchedLength)
        {
            return es.MatchedLength > en.MatchedLength
                ? (es.Head, es.MatchedLength, 2)
                : (en.Head, en.MatchedLength, 1);
        }

        bool spanishHint = span.Any(c => SpanishOnly.Contains(c));
        return spanishHint ? (es.Head, es.MatchedLength, 2) : (en.Head, en.MatchedLength, 1);
    }

    /// <summary>
    /// FR-19b. Two things corrupt a captured span silently rather than failing:
    /// Teams scans run past the end of a message into the next one, and embedded
    /// avatars/images arrive as U+FFFC OBJECT REPLACEMENT CHARACTER.
    /// </summary>
    private static string Sanitize(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n', '\u2028', '\u2029']);
        if (nl >= 0) s = s[..nl];
        return s.Replace("\uFFFC", "").Trim();
    }

    private static string FirstWord(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return s;
        // For Japanese there are no spaces, so send the whole span and let Yomitan
        // decide the match length. For Latin scripts, cut at the first space.
        if (s[0] < 0x3000)
        {
            int i = s.IndexOfAny([' ', '\t', ',', '.', ';', ':', '!', '?']);
            if (i > 0) return s[..i];
        }
        return s;
    }

    // ---------------------------------------------------------------- helpers

    private static void Header(string s)
    {
        Console.WriteLine();
        Console.WriteLine("== " + s);
    }

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n] + "...";

    private const int VK_SHIFT = 0x10;
    private const int VK_ESCAPE = 0x1B;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point lpPoint);
}
