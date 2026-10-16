using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Rosetta;

internal sealed record Captured(string Span, string Term, Rectangle[] Rects, string Process);

/// <summary>
/// FR-04 / FR-05. Reads the text under the cursor through UI Automation.
/// Every rule here came out of the spike measurements rather than the API docs.
/// </summary>
internal sealed class Capture : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly int _scanLength;

    public Capture(int scanLength = 16) => _scanLength = scanLength;

    public Captured? At(Point pt)
    {
        var element = _automation.FromPoint(pt);
        if (element is null) return null;

        string proc = "?";
        try { proc = System.Diagnostics.Process.GetProcessById(element.Properties.ProcessId.Value).ProcessName; }
        catch { /* the process can vanish mid-probe */ }

        // On Chromium-rendered content the element under the cursor is very often a
        // styled span or Group with no TextPattern, while the text itself lives on an
        // ancestor. The feasibility spike walked up to find it; without that walk
        // almost all web content fell through to the degraded Name path below and
        // produced a single word.
        var textPattern = element.Patterns.Text.PatternOrDefault;
        FlaUI.Core.Patterns.ITextPattern? documentPattern = null;

        if (textPattern is not null)
        {
            var direct = FromTextPattern(textPattern, pt, proc);
            if (direct is not null) return direct;
        }
        else
        {
            // Walk up for a text pattern. The cap used to be 5, which on a deep page is
            // nowhere near the Document node - so headings, links and styled markup fell
            // through to the Name fallback and had their word GUESSED from cursor
            // position. Chromium exposes a Document-level text pattern covering the whole
            // page, and a range taken from it resolves at the cursor with real rectangles.
            try
            {
                var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
                var cur = walker.GetParent(element);
                for (int depth = 1; depth <= 30 && cur is not null; depth++)
                {
                    var p = cur.Patterns.Text.PatternOrDefault;
                    if (p is not null)
                    {
                        bool isDocument = cur.Properties.ControlType.ValueOrDefault == ControlType.Document;
                        if (isDocument) { documentPattern = p; Log.Write($"    text pattern: Document at depth {depth}"); break; }

                        if (textPattern is null)
                        {
                            textPattern = p;
                            Log.Write($"    text pattern: ancestor depth {depth} ({cur.Properties.ControlType.ValueOrDefault})");
                        }
                    }
                    cur = walker.GetParent(cur);
                }
            }
            catch { /* the tree can change under us mid-walk */ }

            // Nearest ancestor first - it is the most specific. The document-wide range is
            // the fallback, since on a huge page it is the coarsest thing available.
            if (textPattern is not null)
            {
                var captured = FromTextPattern(textPattern, pt, proc);
                if (captured is not null) return captured;
            }
            if (documentPattern is not null)
            {
                var captured = FromTextPattern(documentPattern, pt, proc);
                if (captured is not null) return captured;
            }
        }

        // FR-05. Measured on Teams: sent messages (fui-ChatMyMessage) expose no
        // TextPattern at all, on themselves or any ancestor, and carry their text only
        // in Name. Without this fallback a large share of any conversation is unreadable.
        string name = element.Properties.Name.ValueOrDefault ?? "";
        string nameSpan = SanitizeSpan(name);
        if (nameSpan.Length == 0) return null;
        Log.Write("    NO text pattern anywhere - estimating the word from cursor position");

        var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
        string term = Lang.IsJapanese(nameSpan)
            ? JapaneseAtEstimate(nameSpan, pt, bounds)
            : NearestWord(nameSpan, pt, bounds);
        if (term.Length == 0) return null;
        return new Captured(nameSpan, term, [bounds], proc);
    }

    // Selecting more than this is a translation request, not a dictionary lookup, so it
    // is left alone rather than becoming an accidental query.
    private const int MaxSelectionChars = 100;

    /// <summary>
    /// FR-15b. A selected phrase wins over the word under the cursor, so a two-word
    /// concept can be looked up as one term. Uses the same TextPattern the capture path
    /// already holds, so there is no new mechanism and no new modifier - Shift is
    /// already carrying enough meaning.
    /// </summary>
    private Captured? FromSelection(FlaUI.Core.Patterns.ITextPattern textPattern, Point pt, string proc)
    {
        try
        {
            var selection = textPattern.GetSelection();
            if (selection is null || selection.Length == 0) return null;

            string text = SanitizeSpan(selection[0].GetText(MaxSelectionChars + 1) ?? "");
            if (text.Length == 0 || text.Length > MaxSelectionChars) return null;
            if (!text.Contains(' ') && !Lang.IsJapanese(text)) return null;   // single word: ordinary path is better

            var rects = selection[0].GetBoundingRectangles();
            // Only honour the selection when the cursor is actually inside it, otherwise
            // a stale selection elsewhere in the document would hijack every lookup.
            if (!RectsContainX(rects, pt)) return null;

            return new Captured(text, text, rects, proc);
        }
        catch
        {
            return null;   // not every provider implements GetSelection
        }
    }

    private Captured? FromTextPattern(FlaUI.Core.Patterns.ITextPattern textPattern, Point pt, string proc)
    {
        var selected = FromSelection(textPattern, pt, proc);
        if (selected is not null) return selected;

        var charRange = textPattern.RangeFromPoint(pt);
        if (charRange is null) return null;

        charRange.ExpandToEnclosingUnit(TextUnit.Character);

        // RangeFromPoint snaps to the NEAREST character, so a cursor resting in the
        // space before a word lands on that word and the lookup drifts rightward.
        // Pointing at a gap should produce nothing at all.
        string here = charRange.GetText(4) ?? "";
        if (here.Length == 0 || char.IsWhiteSpace(here[0])) return null;

        var range = charRange.Clone();
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, _scanLength);
        string raw = range.GetText(_scanLength + 8) ?? "";
        string span = SanitizeSpan(raw);
        if (span.Length == 0) return null;

        // Forward-only scanning is correct for Japanese - it is what Yomitan does - but
        // clips Latin words when the cursor lands mid-word: "construccion" captures as
        // "onstruccion".
        if (!Lang.IsJapanese(span))
        {
            // Clone the range we already resolved rather than calling RangeFromPoint a
            // second time. Two calls for the same point can resolve to DIFFERENT places
            // when the text pattern came from an ancestor covering the whole document -
            // observed in the trace as span="y bag help is it" yielding term=
            // "accidentally" from a line 340px away.
            var wordRange = charRange.Clone();
            if (wordRange is null) return null;

            wordRange.ExpandToEnclosingUnit(TextUnit.Word);
            string word = SanitizeTerm(wordRange.GetText(64) ?? "");
            var wordRects = wordRange.GetBoundingRectangles();

            // ExpandToEnclosingUnit(Word) can still move off the character we probed.
            // Where rectangles are available, require the word to sit under the cursor.
            if (word.Length > 0 && RectsContainX(wordRects, pt))
                return new Captured(span, FirstWord(word), wordRects, proc);

            return null;
        }

        return new Captured(span, SanitizeTerm(span), range.GetBoundingRectangles(), proc);
    }

    private static bool RectsContainX(Rectangle[] rects, Point pt)
    {
        if (rects.Length == 0) return true;   // nothing to contradict; placement falls back to the cursor
        foreach (var r in rects)
            if (pt.X >= r.Left - 2 && pt.X <= r.Right + 2 && pt.Y >= r.Top - 2 && pt.Y <= r.Bottom + 2)
                return true;
        return false;
    }

    /// <summary>
    /// Name-fallback positioning. Without a text range we only know the element box, so
    /// the word under the cursor has to be estimated from horizontal position.
    ///
    /// Estimate by CHARACTER index, not word index. Word-index estimation assumes every
    /// word is equally wide, which is badly wrong in practice: hovering "bag" in
    /// "I found this in my bag help is it a tracking device?" estimated into "this".
    /// Character widths are roughly uniform, so the character estimate lands far closer,
    /// and expanding to the surrounding word boundary recovers the whole word.
    ///
    /// Still approximate, and still wrong for wrapped text, where horizontal position
    /// says nothing about which line the cursor is on.
    /// </summary>
    private static string NearestWord(string name, Point pt, Rectangle box)
    {
        if (name.Length == 0) return "";
        if (box.Width <= 0) return FirstWord(name);

        double frac = Math.Clamp((pt.X - box.X) / (double)box.Width, 0, 0.999);
        int idx = Math.Clamp((int)(frac * name.Length), 0, name.Length - 1);

        // If the estimate lands in a gap, step to the nearest character either side.
        if (char.IsWhiteSpace(name[idx]))
        {
            int fwd = idx, back = idx;
            while (fwd < name.Length && char.IsWhiteSpace(name[fwd])) fwd++;
            while (back >= 0 && char.IsWhiteSpace(name[back])) back--;
            bool forwardCloser = (fwd - idx) <= (idx - back);
            idx = forwardCloser && fwd < name.Length ? fwd : Math.Max(back, 0);
            if (idx >= name.Length || char.IsWhiteSpace(name[idx])) return "";
        }

        int start = idx, end = idx;
        while (start > 0 && !char.IsWhiteSpace(name[start - 1])) start--;
        while (end < name.Length && !char.IsWhiteSpace(name[end])) end++;
        if (end <= start) return "";

        return name[start..end].Trim(' ', '.', ',', ';', ':', '!', '?', '"', '(', ')');
    }

    /// <summary>
    /// The Japanese equivalent of the same problem. Sending the element's whole Name
    /// makes Yomitan match from its first character, so a long link always returned its
    /// opening word regardless of where the cursor was. Slice a scan window starting at
    /// the estimated character instead.
    /// </summary>
    private string JapaneseAtEstimate(string name, Point pt, Rectangle box)
    {
        if (name.Length == 0) return "";
        if (box.Width <= 0) return name[..Math.Min(_scanLength, name.Length)];

        double frac = Math.Clamp((pt.X - box.X) / (double)box.Width, 0, 0.999);
        int idx = Math.Clamp((int)(frac * name.Length), 0, name.Length - 1);
        return name.Substring(idx, Math.Min(_scanLength, name.Length - idx));
    }

    /// <summary>
    /// FR-19b, for the LOOKUP TERM. Cutting at the first newline stops Teams scans
    /// running past the end of a message into the next one. Embedded avatars and images
    /// arrive as U+FFFC OBJECT REPLACEMENT CHARACTER and must go.
    /// </summary>
    public static string SanitizeTerm(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n', '\u2028', '\u2029']);
        if (nl >= 0) s = s[..nl];
        return s.Replace("\uFFFC", "").Trim();
    }

    /// <summary>
    /// FR-19b, for the LANGUAGE-DETECTION SPAN. Here more context is strictly better -
    /// Lang.SpanishBias scores whole sentences - so newlines are flattened to spaces
    /// rather than truncating. Applying the term rule here was discarding everything
    /// after line one on headings and wrapped blocks, which is what reduced web content
    /// to a single word.
    /// </summary>
    public static string SanitizeSpan(string s) =>
        s.Replace("\uFFFC", "")
         .Replace('\r', ' ').Replace('\n', ' ')
         .Replace('\u2028', ' ').Replace('\u2029', ' ')
         .Trim();

    /// <summary>Kept for callers that only need the term rule.</summary>
    public static string Sanitize(string s) => SanitizeTerm(s);

    private static string FirstWord(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return s;
        int i = s.IndexOfAny([' ', '\t', ',', '.', ';', ':', '!', '?', '"', '(', ')']);
        return i > 0 ? s[..i] : s;
    }

    public void Dispose() => _automation.Dispose();
}

/// <summary>FR-10. Which Yomitan profile should answer for this text.</summary>
internal static class Lang
{
    public const int Japanese = 0;
    public const int English = 1;
    public const int Spanish = 2;

    public static bool IsJapanese(string s)
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

    private const string SpanishChars = "ñáéíóúü¿¡";

    // Scoring the SPAN rather than the word is what fixes short function words.
    // Measured failure: "La" in a Spanish sentence resolved to English, because the
    // match lengths tied and no accented character happened to be nearby.
    private static readonly string[] SpanishStops =
        ["la", "el", "los", "las", "una", "uno", "que", "de", "del", "es", "en", "por",
         "para", "con", "como", "pero", "su", "sus", "lo", "al", "se", "no", "mas"];

    private static readonly string[] EnglishStops =
        ["the", "is", "are", "was", "were", "of", "and", "to", "in", "that", "it",
         "for", "with", "as", "this", "which", "be", "have", "has", "but", "not"];

    /// <summary>
    /// Returns a Spanish-minus-English score for the span. Positive means Spanish.
    /// Used only to break ties; the lookup itself is still the primary signal.
    /// </summary>
    public static int SpanishBias(string span)
    {
        int score = 0;
        foreach (char c in span)
            if (SpanishChars.Contains(c)) score += 3;

        var words = span.ToLowerInvariant().Split([' ', '\t', ',', '.', ';', ':', '!', '?'],
            StringSplitOptions.RemoveEmptyEntries);

        // The span is a forward scan from the character under the cursor, so its FIRST
        // token is usually a word fragment and its LAST is usually cut off. Scoring
        // those produced real misreads: the fragment "es" (from "does") is a Spanish
        // stopword and sent an English lookup to the Spanish profile. Only interior
        // tokens are whole words.
        int from = words.Length > 2 ? 1 : 0;
        int to = words.Length > 2 ? words.Length - 1 : words.Length;

        for (int i = from; i < to; i++)
        {
            if (Array.IndexOf(SpanishStops, words[i]) >= 0) score += 2;
            if (Array.IndexOf(EnglishStops, words[i]) >= 0) score -= 2;
        }
        return score;
    }
}
