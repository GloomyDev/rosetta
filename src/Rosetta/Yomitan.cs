using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Rosetta;

/// <summary>
/// One dictionary's worth of definitions. A profile may have several dictionaries
/// enabled at once - an English profile carrying wty-en-en, wty-en-ja and wty-en-es
/// answers "what does this mean" and "how do I say it in Japanese" from a single
/// lookup, which is what makes cross-language scale without a profile per direction.
/// </summary>
internal sealed record DefinitionGroup(
    string Dictionary,
    string? TargetLanguage,
    IReadOnlyList<string> Glosses);

/// <summary>One lookup result, enough to paint the popup and highlight the source text.</summary>
internal sealed record Lookup(
    string Term,
    string Reading,
    int MatchedLength,
    string Dictionary,
    IReadOnlyList<string> InflectionRules,
    IReadOnlyList<string> PlainGlosses,
    IReadOnlyList<DefinitionGroup> Groups,
    int ProfileIndex);

/// <summary>
/// Adapter over the Yomitan API. ALL knowledge of the /termEntries response shape lives
/// here: that format is explicitly undocumented and unstable ("no guarantees are made
/// for the stability or accuracy of the responses"), so it must not leak into the rest
/// of the app.
/// </summary>
internal sealed class Yomitan
{
    private readonly HttpClient _http;

    public Yomitan(string baseAddress = "http://127.0.0.1:19633", int timeoutMs = 3000)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
        };
    }

    /// <summary>
    /// The API host is Python's single-threaded http.server and reads the body strictly
    /// by Content-Length. JsonContent streams without one, the host then reads an empty
    /// body and answers 500 "{}". Always serialize up front.
    /// </summary>
    private async Task<string?> PostAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            string payload = JsonSerializer.Serialize(body);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/" + path, content, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> IsAliveAsync(CancellationToken ct = default) =>
        await PostAsync("yomitanVersion", new { }, ct) is not null;

    /// <summary>
    /// Guards against Edge silently reverting to the Microsoft Store build of Yomitan,
    /// which ignores profileIndex and would make every language resolve as Japanese.
    /// A valid request must succeed first, otherwise a totally dead API would look
    /// like a correctly-rejecting patched one.
    /// </summary>
    public async Task<bool> IsPatchedAsync(CancellationToken ct = default)
    {
        if (await PostAsync("termEntries", new { term = "test", profileIndex = 0 }, ct) is null) return false;
        return await PostAsync("termEntries", new { term = "test", profileIndex = 99 }, ct) is null;
    }

    public async Task<Lookup?> LookupAsync(string term, int profileIndex, CancellationToken ct)
    {
        string? json = await PostAsync("termEntries", new { term, profileIndex }, ct);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("dictionaryEntries", out var entries) ||
                entries.GetArrayLength() == 0)
            {
                return null;
            }

            var e = entries[0];
            int matched = e.TryGetProperty("maxOriginalTextLength", out var m) ? m.GetInt32() : 0;

            string headTerm = "", reading = "";
            if (e.TryGetProperty("headwords", out var hws) && hws.GetArrayLength() > 0)
            {
                var h = hws[0];
                headTerm = h.TryGetProperty("term", out var t) ? t.GetString() ?? "" : "";
                reading = h.TryGetProperty("reading", out var r) ? r.GetString() ?? "" : "";
            }

            var rules = new List<string>();
            if (e.TryGetProperty("inflectionRuleChainCandidates", out var cands))
            {
                foreach (var c in cands.EnumerateArray())
                {
                    if (!c.TryGetProperty("inflectionRules", out var rs)) continue;
                    foreach (var r in rs.EnumerateArray())
                        if (r.TryGetProperty("name", out var n) && n.GetString() is { } s && !rules.Contains(s))
                            rules.Add(s);
                }
            }

            // Keep EVERY definition group, not just the first: with several dictionaries
            // enabled on one profile, the later groups are the other-language
            // equivalents, which is the whole point of cross-language lookup.
            string dict = "";
            var groups = new List<DefinitionGroup>();
            if (e.TryGetProperty("definitions", out var defs))
            {
                foreach (var d in defs.EnumerateArray())
                {
                    string name = d.TryGetProperty("dictionary", out var dn) ? dn.GetString() ?? "" : "";
                    if (dict.Length == 0) dict = name;

                    var glosses = new List<string>();
                    if (d.TryGetProperty("entries", out var ents))
                        foreach (var ent in ents.EnumerateArray())
                            CollectText(ent, glosses);

                    var trimmed = Trim(glosses);
                    if (trimmed.Count == 0) continue;

                    var existing = groups.FindIndex(g => g.Dictionary == name);
                    if (existing >= 0)
                    {
                        var merged = new List<string>(groups[existing].Glosses);
                        foreach (var g in trimmed) if (!merged.Contains(g)) merged.Add(g);
                        groups[existing] = groups[existing] with { Glosses = merged };
                    }
                    else
                    {
                        groups.Add(new DefinitionGroup(name, TargetLanguageOf(name), trimmed));
                    }
                }
            }

            var first = groups.Count > 0 ? groups[0].Glosses : [];
            return new Lookup(headTerm, reading, matched, dict, rules, first, groups, profileIndex);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Which language a dictionary's glosses are written in. Wiktionary exports are
    /// named wty-{source}-{target}; Jitendex is Japanese headwords glossed in English.
    /// Returns null when the naming gives nothing away, in which case the UI just shows
    /// the dictionary name rather than guessing.
    /// </summary>
    private static string? TargetLanguageOf(string dictionary)
    {
        if (dictionary.StartsWith("wty-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = dictionary.Split('-');
            if (parts.Length >= 3 && parts[2].Length >= 2) return parts[2][..2].ToLowerInvariant();
        }
        if (dictionary.StartsWith("Jitendex", StringComparison.OrdinalIgnoreCase)) return "en";
        if (dictionary.StartsWith("JMdict", StringComparison.OrdinalIgnoreCase)) return "en";
        return null;
    }

    /// <summary>
    /// Every installed dictionary returns `structured-content`: a nested tag tree rather
    /// than plain glosses. Rendering it faithfully is /ankiFields' job; for the fast
    /// first paint we just harvest the text nodes.
    /// </summary>
    private static void CollectText(JsonElement node, List<string> into, int depth = 0)
    {
        if (depth > 24 || into.Count > 40) return;

        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                string s = node.GetString() ?? "";
                s = s.Trim();
                if (s.Length > 0) into.Add(s);
                break;
            case JsonValueKind.Array:
                foreach (var c in node.EnumerateArray()) CollectText(c, into, depth + 1);
                break;
            case JsonValueKind.Object:
                if (node.TryGetProperty("content", out var content)) CollectText(content, into, depth + 1);
                break;
        }
    }

    private static List<string> Trim(List<string> glosses)
    {
        var outp = new List<string>();
        foreach (var g in glosses)
        {
            if (g.Length <= 1) continue;             // stray punctuation from the tree
            if (outp.Contains(g)) continue;
            outp.Add(g);
            if (outp.Count >= 8) break;
        }
        return outp;
    }

    /// <summary>
    /// Second paint. Returns Yomitan's own rendered glossary HTML plus furigana ruby.
    /// Deliberately never requests the `audio` marker: that makes Yomitan fetch from
    /// remote audio sources, which would break the local-only guarantee in NFR-06.
    /// </summary>
    public async Task<(string? Glossary, string? Furigana)> RichAsync(string text, int profileIndex, CancellationToken ct)
    {
        string? json = await PostAsync("ankiFields", new
        {
            text,
            type = "term",
            markers = new[] { "glossary", "furigana", "pitch-accents", "frequencies" },
            maxEntries = 3,
            includeMedia = false,
            profileIndex,
        }, ct);
        if (json is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.GetArrayLength() == 0)
                return (null, null);
            var f = fields[0];
            string? g = f.TryGetProperty("glossary", out var gv) ? gv.GetString() : null;
            string? fu = f.TryGetProperty("furigana", out var fv) ? fv.GetString() : null;
            return (string.IsNullOrWhiteSpace(g) ? null : g, string.IsNullOrWhiteSpace(fu) ? null : fu);
        }
        catch
        {
            return (null, null);
        }
    }
}
