using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Tango;

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

        var textPattern = element.Patterns.Text.PatternOrDefault;
        if (textPattern is not null)
        {
            var range = textPattern.RangeFromPoint(pt);
            if (range is not null)
            {
                range.ExpandToEnclosingUnit(TextUnit.Character);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, _scanLength);
                string span = Sanitize(range.GetText(_scanLength + 8) ?? "");
                var rects = range.GetBoundingRectangles();

                if (span.Length == 0) return null;

                // Forward-only scanning is correct for Japanese - it is what Yomitan
                // does - but clips Latin words when the cursor lands mid-word:
                // "construccion" captures as "onstruccion".
                if (!Lang.IsJapanese(span))
                {
                    var wordRange = textPattern.RangeFromPoint(pt);
                    if (wordRange is not null)
                    {
                        wordRange.ExpandToEnclosingUnit(TextUnit.Word);
                        string word = Sanitize(wordRange.GetText(64) ?? "");
                        if (word.Length > 0)
                            return new Captured(span, FirstWord(word), wordRange.GetBoundingRectangles(), proc);
                    }
                }
                return new Captured(span, span, rects, proc);
            }
        }

        // FR-05. Measured on Teams: sent messages (fui-ChatMyMessage) expose no
        // TextPattern at all and carry their text only in Name. Without this fallback
        // a large share of any conversation is unreadable.
        string name = Sanitize(element.Properties.Name.ValueOrDefault ?? "");
        if (name.Length == 0) return null;

        var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
        string term = Lang.IsJapanese(name) ? name : NearestWord(name, pt, bounds);
        if (term.Length == 0) return null;
        return new Captured(name, term, [bounds], proc);
    }

    /// <summary>
    /// Name-fallback positioning: without a text range we only know the element box, so
    /// estimate which word the cursor is over from its horizontal position. Crude, and
    /// wrong for wrapped text - but it beats showing nothing.
    /// </summary>
    private static string NearestWord(string name, Point pt, Rectangle box)
    {
        var words = name.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        if (box.Width <= 0) return words[0];

        double frac = Math.Clamp((pt.X - box.X) / (double)box.Width, 0, 0.999);
        return words[(int)(frac * words.Length)];
    }

    /// <summary>
    /// FR-19b. Two things corrupt a span silently rather than failing: Teams scans run
    /// past the end of a message into the next one, and embedded avatars and images
    /// arrive as U+FFFC OBJECT REPLACEMENT CHARACTER.
    /// </summary>
    public static string Sanitize(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n', '\u2028', '\u2029']);
        if (nl >= 0) s = s[..nl];
        return s.Replace("\uFFFC", "").Trim();
    }

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

        foreach (var w in span.ToLowerInvariant().Split([' ', '\t', ',', '.', ';', ':', '!', '?'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (Array.IndexOf(SpanishStops, w) >= 0) score += 2;
            if (Array.IndexOf(EnglishStops, w) >= 0) score -= 2;
        }
        return score;
    }
}
