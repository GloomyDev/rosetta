using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Rosetta;

/// <summary>
/// Online translation through Google's key-less "gtx" endpoint. This is the ONE part of
/// Rosetta that is not local: the text is sent to Google. The endpoint is unofficial, so
/// all knowledge of its response shape stays in this class (same rule as Yomitan.cs).
///
/// Measured while building this: the endpoint answers 429 now and then even at low
/// volume, and the same request succeeds a moment later, so a throttled call is retried
/// briefly before giving up.
/// </summary>
internal static class Translator
{
    private const string Url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t&dj=0&tl=";

    // Longer selections are cut here; the popup says so.
    public const int MaxChars = 3000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public sealed record Result(string Text, string Detected, string Target);

    public static async Task<Result?> TranslateAsync(string text, string target, CancellationToken ct)
    {
        var first = await RequestAsync(text, target, ct);
        if (first is null) return null;

        // Already in the target language: translating would just echo it back, so use the
        // next most likely language for this user instead.
        if (first.Detected == target)
        {
            string alt = target == "en" ? "es" : "en";
            return await RequestAsync(text, alt, ct) ?? first;
        }
        return first;
    }

    private static async Task<Result?> RequestAsync(string text, string target, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(350 * attempt, ct);
            try
            {
                using var body = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]);
                using var resp = await Http.PostAsync(Url + target, body, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests) continue;
                if (!resp.IsSuccessStatusCode) return null;

                var parsed = Parse(await resp.Content.ReadAsStringAsync(ct), target);
                if (parsed is not null) return parsed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Write($"  translate failed: {ex.GetType().Name}: {ex.Message}");
                return null;     // offline / timeout: retrying will not help
            }
        }
        return null;
    }

    /// <summary>Shape: [[["translated","original",...], ...], null, "detected-lang", ...]</summary>
    private static Result? Parse(string json, string target)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            foreach (var seg in root[0].EnumerateArray())
                if (seg.ValueKind == JsonValueKind.Array && seg[0].ValueKind == JsonValueKind.String)
                    sb.Append(seg[0].GetString());
            if (sb.Length == 0) return null;

            string detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
                ? root[2].GetString()! : "";
            return new Result(sb.ToString(), detected, target);
        }
        catch (Exception ex)
        {
            Log.Write($"  translate parse failed: {ex.Message}");
            return null;
        }
    }

    public static string LanguageName(string code) => code switch
    {
        "en" => "English",
        "ja" => "Japanese",
        "es" => "Spanish",
        "" => "?",
        _ => code.ToUpperInvariant(),
    };
}
