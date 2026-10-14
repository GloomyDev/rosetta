using System.Net;
using System.Text;

namespace Tango;

/// <summary>
/// FR-12. Two paints: a fast one from /termEntries so the popup appears on the ~25ms
/// path, and a rich one that swaps in Yomitan's own glossary HTML when /ankiFields
/// returns. /ankiFields does NOT return any CSS, so the styling below is ours.
/// </summary>
internal static class Html
{
    private const string Css = """
        :root {
          color-scheme: light dark;
          --bg: #ffffff; --fg: #1a1a1a; --muted: #6b7280;
          --rule: #e5e7eb; --accent: #2563eb; --tagbg: #f3f4f6;
        }
        @media (prefers-color-scheme: dark) {
          :root { --bg: #1f2430; --fg: #e6e6e6; --muted: #9aa3b2;
                  --rule: #343b4a; --accent: #7aa2f7; --tagbg: #2a3040; }
        }
        html, body { margin: 0; padding: 0; background: var(--bg); color: var(--fg); }
        body {
          font: 14px/1.55 "Segoe UI", "Yu Gothic UI", "Meiryo", system-ui, sans-serif;
          padding: 10px 12px;
          overflow-wrap: anywhere;
        }
        .head { display: flex; align-items: baseline; gap: .5em; flex-wrap: wrap; }
        .term { font-size: 20px; font-weight: 600; }
        .term ruby rt { font-size: .5em; font-weight: 400; color: var(--muted); }
        .reading { color: var(--muted); font-size: 14px; }
        .tags { margin: 4px 0 8px; display: flex; gap: 4px; flex-wrap: wrap; }
        .tag {
          font-size: 11px; padding: 1px 6px; border-radius: 4px;
          background: var(--tagbg); color: var(--muted); white-space: nowrap;
        }
        .tag.infl { color: var(--accent); }
        .glosses { margin: 0; padding-left: 1.2em; }
        .glosses li { margin: 2px 0; }
        .dict { margin-top: 8px; padding-top: 6px; border-top: 1px solid var(--rule);
                font-size: 11px; color: var(--muted); }
        .pending { color: var(--muted); font-style: italic; font-size: 12px; }

        /* Yomitan's own rendered glossary (second paint). */
        .yomitan-glossary { text-align: left; }
        .yomitan-glossary ol, .yomitan-glossary ul { margin: 2px 0; padding-left: 1.3em; }
        .yomitan-glossary li { margin: 2px 0; }
        .yomitan-glossary rt { font-size: .6em; color: var(--muted); }
        .yomitan-glossary details { margin: 2px 0; }
        .yomitan-glossary summary { cursor: default; color: var(--muted); font-size: 12px; }
        .yomitan-glossary [data-sc-class="tag"],
        .yomitan-glossary span[title] { font-size: .85em; }
        .yomitan-glossary img { max-width: 100%; height: auto; }
        """;

    private const string BackBarCss = """
        .backbar { display: flex; align-items: center; gap: 6px; margin: -4px 0 6px; }
        .back {
          cursor: pointer; border: 0; background: var(--tagbg); color: var(--fg);
          font: inherit; font-size: 12px; line-height: 1;
          padding: 3px 9px; border-radius: 4px;
        }
        .back:hover { background: var(--accent); color: #fff; }
        .depth { font-size: 11px; color: var(--muted); }
        ::selection { background: transparent; }
        """;

    /// <summary>
    /// FR-15. Nested lookup inside the popup. Done in the page rather than through UIA:
    /// real text nodes give exact offsets and rectangles, and reusing the same
    /// forward-N-characters rule as FR-04 keeps nested lookups behaving like top-level
    /// ones. Keyboard events never arrive here - the window is WS_EX_NOACTIVATE and
    /// never takes focus - so Back is a click target, and the host watches for the
    /// keyboard shortcut globally instead.
    /// </summary>
    private const string Script = """
        <script>
        (function () {
          const SCAN = 16;
          const JA = /[぀-ヿ㐀-䶿一-鿿ｦ-ﾝ]/;
          const WORD = /[\p{L}\p{M}'’-]/u;
          let lastTerm = null, timer = null;

          function rectOf(node, s, e) {
            const r = document.createRange();
            r.setStart(node, s); r.setEnd(node, e);
            const b = r.getBoundingClientRect();
            return { x: b.left, y: b.top, w: b.width, h: b.height };
          }

          function at(x, y) {
            const caret = document.caretRangeFromPoint
              ? document.caretRangeFromPoint(x, y)
              : null;
            if (!caret) return null;
            const node = caret.startContainer;
            if (!node || node.nodeType !== 3) return null;

            const text = node.data;
            const off = Math.min(caret.startOffset, Math.max(0, text.length - 1));
            if (!text || text.trim().length === 0) return null;

            const ahead = text.slice(off, off + SCAN);
            const context = text.slice(Math.max(0, off - 40), off + 40);

            if (JA.test(ahead)) {
              if (!ahead.trim()) return null;
              return { term: ahead, span: ahead, rect: rectOf(node, off, Math.min(off + 1, text.length)) };
            }
            let s = off, e = off;
            while (s > 0 && WORD.test(text[s - 1])) s--;
            while (e < text.length && WORD.test(text[e])) e++;
            if (e - s < 2) return null;
            return { term: text.slice(s, e), span: context, rect: rectOf(node, s, e) };
          }

          function send(obj) { window.chrome.webview.postMessage(JSON.stringify(obj)); }

          document.addEventListener('mousemove', function (ev) {
            if (!ev.shiftKey) { lastTerm = null; return; }
            if (ev.target && ev.target.closest && ev.target.closest('.backbar')) return;
            clearTimeout(timer);
            const x = ev.clientX, y = ev.clientY;
            timer = setTimeout(function () {
              const hit = at(x, y);
              if (!hit || hit.term === lastTerm) return;
              lastTerm = hit.term;
              send({ action: 'lookup', term: hit.term, span: hit.span, rect: hit.rect });
            }, 20);
          });

          document.addEventListener('click', function (ev) {
            const b = ev.target && ev.target.closest && ev.target.closest('.back');
            if (b) { ev.preventDefault(); send({ action: 'back' }); }
          });

          // The host drives this when Shift is held over the popup. The page cannot
          // detect Shift on its own: the window never takes focus, so no key events
          // ever arrive here.
          window.chrome.webview.addEventListener('message', function (ev) {
            let m = ev.data;
            if (typeof m === 'string') { try { m = JSON.parse(m); } catch (e) { return; } }
            if (!m || m.action !== 'probe') return;
            const hit = at(m.x, m.y);
            if (!hit || hit.term === lastTerm) return;
            lastTerm = hit.term;
            send({ action: 'lookup', term: hit.term, span: hit.span, rect: hit.rect });
          });
        })();
        </script>
        """;

    private const string Shell = """
        <!doctype html><html><head><meta charset="utf-8">
        <style>{CSS}</style></head><body>{BODY}{SCRIPT}</body></html>
        """;

    public static string Page(string body, int depth = 0)
    {
        if (depth > 0)
        {
            body = $"""<div class="backbar"><button class="back">&#8592; back</button>""" +
                   $"""<span class="depth">{depth} level{(depth == 1 ? "" : "s")} deep</span></div>""" + body;
        }
        return Shell
            .Replace("{CSS}", Css + BackBarCss)
            .Replace("{BODY}", body)
            .Replace("{SCRIPT}", Script);
    }

    /// <summary>First paint: everything already in hand from /termEntries.</summary>
    public static string FirstPaint(Lookup l, int depth = 0)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"head\"><span class=\"term\">").Append(E(l.Term)).Append("</span>");
        if (!string.IsNullOrEmpty(l.Reading) && l.Reading != l.Term)
            sb.Append("<span class=\"reading\">").Append(E(l.Reading)).Append("</span>");
        sb.Append("</div>");

        if (l.InflectionRules.Count > 0)
        {
            sb.Append("<div class=\"tags\">");
            foreach (var r in l.InflectionRules)
                sb.Append("<span class=\"tag infl\">").Append(E(r)).Append("</span>");
            sb.Append("</div>");
        }

        if (l.PlainGlosses.Count > 0)
        {
            sb.Append("<ul class=\"glosses\">");
            foreach (var g in l.PlainGlosses)
                sb.Append("<li>").Append(E(g)).Append("</li>");
            sb.Append("</ul>");
        }
        else
        {
            sb.Append("<div class=\"pending\">loading definitions...</div>");
        }

        sb.Append("<div class=\"dict\">").Append(E(l.Dictionary))
          .Append(" · ").Append(LangName(l.ProfileIndex)).Append("</div>");
        return Page(sb.ToString(), depth);
    }

    /// <summary>Second paint: Yomitan's rendered glossary, swapped in place.</summary>
    public static string RichPaint(Lookup l, string glossary, string? furigana, int depth = 0)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"head\"><span class=\"term\">")
          .Append(string.IsNullOrEmpty(furigana) ? E(l.Term) : furigana)   // ruby markup, trusted
          .Append("</span>");
        if (string.IsNullOrEmpty(furigana) && !string.IsNullOrEmpty(l.Reading) && l.Reading != l.Term)
            sb.Append("<span class=\"reading\">").Append(E(l.Reading)).Append("</span>");
        sb.Append("</div>");

        if (l.InflectionRules.Count > 0)
        {
            sb.Append("<div class=\"tags\">");
            foreach (var r in l.InflectionRules)
                sb.Append("<span class=\"tag infl\">").Append(E(r)).Append("</span>");
            sb.Append("</div>");
        }

        sb.Append(glossary);
        sb.Append("<div class=\"dict\">").Append(E(l.Dictionary))
          .Append(" · ").Append(LangName(l.ProfileIndex)).Append("</div>");
        return Page(sb.ToString(), depth);
    }

    /// <summary>FR-20: say which state this is, never fail silently.</summary>
    public static string Message(string text, string? detail = null)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"pending\">").Append(E(text)).Append("</div>");
        if (detail is not null)
            sb.Append("<div class=\"dict\">").Append(E(detail)).Append("</div>");
        return Page(sb.ToString());
    }

    private static string LangName(int profile) => profile switch
    {
        Lang.Japanese => "Japanese",
        Lang.English => "English",
        Lang.Spanish => "Spanish",
        _ => "?",
    };

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
