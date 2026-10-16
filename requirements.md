# Rosetta — System-Wide Pop-up Dictionary — Requirements

Oct 14, 2026 · @ぺぺ

## Purpose and scope

The tool gives Yomitan's browser experience — hold Shift, hover a word, see a dictionary popup — inside native Windows desktop apps. The first targets are Microsoft Teams (desktop client), Microsoft Word (desktop) and PDF viewers.

It must work for Japanese, English and Spanish from day one, choosing the language automatically from the text under the cursor, and for any other language Yomitan supports with configuration rather than code. It reads real on-screen text through the OS accessibility layer; it is not an OCR tool for games, images or video.

## Assumptions and constraints

- **Platform:** Windows 10/11, x64. macOS and Linux are not targeted in v1.
- **Implementation:** C#/.NET. Chosen for native UIA COM interop, low latency, first-class WebView2, and a single per-user executable (NFR-09).
- **Text source:** Microsoft UI Automation (UIA), primarily `TextPattern.RangeFromPoint`, with `ElementFromPoint().Name` as a fallback.
- **Lookup backend:** the Yomitan API at `http://127.0.0.1:19633`, provided by [yomidevs/yomitan-api](https://github.com/yomidevs/yomitan-api). Yomitan supplies dictionaries, deinflection and multi-language parsing; the tool does not reimplement them.
- **Patched backend required.** `termEntries` upstream hardcodes the active profile and accepts no language or profile parameter. The tool targets a patched `termEntries` that accepts `profileIndex`, mirroring the existing `ankiCardFormats` parameter. See *Backend dependency* below. Without it, automatic language detection (FR-10) cannot act on what it detects.
- **Dependency chain.** Four things must be true for a lookup to succeed, and each must be separately detectable (FR-20):
  1. Python is on PATH and the native-messaging host from `install_yomitan_api.py` is registered (per-user, HKCU — no admin).
  2. A supported browser is running.
  3. Yomitan is installed, with three profiles configured (Japanese, English, Spanish).
  4. Settings → General → "Enable Yomitan API" is on.
  5. The browser must be restarted after the native host is registered; Edge only reads host registrations at startup.
- **The API host cannot read chunked request bodies.** It reads the body strictly by `Content-Length`. A .NET client using `PostAsJsonAsync`/`JsonContent` streams without one and the host answers `500 "{}"` for every call. Serialize the payload first and send it as a `StringContent`. Verified 2026-10-14: identical payload, chunked → 500 (4 bytes), `Content-Length: 35` → 200 (119,732 bytes).
- **The API host serializes requests.** It is a ~117-line passthrough built on Python's single-threaded `http.server`; a stalled native-messaging round trip blocks every request behind it. This drives the timeout and single-flight rules in NFR-10.
- **Cross-language uses one profile per SOURCE language, not one per direction pair.** A profile's `language` setting drives deinflection and is keyed to the source; the target is merely which dictionary is enabled, and a profile may enable several at once. So three profiles cover all directions between three languages (one per pair would need nine), and a cross-language answer costs no extra round trip. Coverage is asymmetric: `→en` is best everywhere, `en→ja` is adequate (4.2 MB), `ja→es` is 0.1 MB and effectively empty, so Japanese→Spanish should be expected to route through English.
- **`/termEntries` is an unstable internal format.** Its own documentation states that "no guarantees are made for the stability or accuracy of the responses". Pin a known-good Yomitan version and confine all knowledge of this shape to one adapter class.
- **Teams is the new desktop client** (`ms-teams.exe`, WebView2/Chromium). The web version is explicitly not a target.
- **No OCR in the core path.** Scanned PDFs without a text layer are handled only by the optional fallback in Out of scope.

## Backend dependency

The lookup API exposes `/serverVersion`, `/yomitanVersion`, `/termEntries`, `/kanjiEntries`, `/ankiCardFormats`, `/ankiFields` and `/tokenize`. Two matter here.

**`/termEntries`** returns Yomitan's internal entry data, including `maxOriginalTextLength` — the matched length needed to highlight the right span (FR-06). Its handler in `ext/js/comm/yomitan-api.js` hardcodes `optionsContext: {index: optionsFull.profileCurrent}`, so every lookup uses whichever profile is active in the browser. A Japanese profile will not deinflect "running" into "run".

**Both `termEntries` and `ankiFields` need this.** The two are used together — resolve with one, render with the other — so patching only `termEntries` produces a popup that shows the correct definition and then replaces it with one from the wrong language a moment later. `kanjiEntries` has the same flaw and is simply not called yet.

The fix is small and has precedent: the `ankiCardFormats` case, in the same `switch` block of the same file, already accepts an optional `profileIndex` and resolves it as `typeof profileIndex === 'number' ? profileIndex : optionsFull.profileCurrent`. Applying that pattern to `termEntries` is roughly five lines. It will be submitted upstream; if it is not accepted, the fallback is a locally built unpacked Yomitan carrying the patch, in which case the native-messaging manifest must be regenerated because the extension ID changes.

This was **confirmed empirically** on 2026-10-14 against Yomitan 26.9.8.0 in Edge, with three profiles configured. Passing `profileIndex` 0, 1 and 2 to `termEntries` returned byte-identical Japanese results for "running" and "corriendo" — the parameter is silently ignored, exactly as the source predicts. The same `profileIndex` on `ankiCardFormats` correctly distinguished profiles 0–2 and errored on 3, so the precedent works. The patch is therefore required, not merely preferable.

The same session surfaced a sharper problem. With the Japanese profile active, Latin-script input does not return *nothing* — it returns **confident nonsense**, because `enableWanakana` romaji-converts it: "running" resolves to る, and "corriendo" to 垢離 (こり). The tool therefore cannot use "no entries" as a signal that it picked the wrong language, and the English/Spanish disambiguation in FR-10 must never include the Japanese profile among its candidates.

**`/ankiFields`** renders Anki handlebars markers (`expression`, `reading`, `glossary`, `pitch-accents`, `frequencies`, `furigana`) and returns glossary HTML with `class="yomitan-glossary"`, plus base64 `dictionaryMedia` and sanitized dictionary CSS. This supplies the popup's presentation layer, so the tool does not hand-build a renderer for pitch accent, furigana or structured glosses.

It cannot, however, supply the matched length: the `cloze-body`, `cloze-prefix` and `cloze-suffix` markers come back empty, because the API passes no sentence context. `/termEntries` is therefore required for FR-06 regardless of how the popup is rendered.

### Measured latency

25 warm calls each, same machine, Japanese term, Jitendex only:

| Call | p50 | p95 |
| --- | --- | --- |
| `/termEntries` | 27 ms | 34 ms |
| `/termEntries` (from .NET) | 21 ms | 24 ms |
| `/ankiFields`, `maxEntries: 1` | 46 ms | 58 ms |
| `/ankiFields`, `maxEntries: 5` | 71 ms | 101 ms |
| `/tokenize` | 16 ms | 17 ms |
| `/termEntries` + `/ankiFields` sequential | — | ~99 ms avg |

Cost is driven by the **number of entries rendered, not the number of markers** — dropping from five markers to one changed nothing, while `maxEntries` 5 → 1 nearly halved it. Keep `maxEntries` low.

This is the binding constraint on NFR-01. Both calls plus EN/ES disambiguation does not fit in 150 ms. The design response is **progressive rendering**: fire `/termEntries`, draw the popup immediately from that JSON, then fetch `/ankiFields` in the background and swap in the richer HTML when it arrives. The popup appears on the 34 ms path and enriches in place, so the user never waits for the 58 ms one.

### Measured full chain (Word, 2026-10-14)

29 Shift-hovers in Microsoft Word over mixed Japanese, English and Spanish text, via `spike/RosettaSpike`. Cold first probe excluded and reported separately.

| Stage | p50 | p95 | max |
| --- | --- | --- | --- |
| UIA `FromPoint` | 5 ms | 9 ms | 12 ms |
| UIA text extraction | 5 ms | 6 ms | 7 ms |
| Lookup (2 calls for EN/ES, 1 for JA) | 23 ms | 47 ms | 73 ms |
| **TOTAL** | **35 ms** | **59 ms** | **86 ms** |

Cold first probe: 220 ms, against NFR-02's 1 s budget.

**NFR-01 is met for Word with 91 ms of headroom**, even with the English/Spanish path making two sequential lookups. The UIA leg is far cheaper than assumed — about 10 ms of a 150 ms budget — so the lookup dominates and is the only leg worth optimising. The headroom is wide enough that a synchronous `/ankiFields` call would *probably* still fit (59 + 83 = 142 ms p95), but that leaves nothing for render, so progressive rendering stays the design.

## Target applications

All three v1 targets render real text, so UIA should reach it; each must be confirmed in FlaUInspect before implementation. They are sequenced rather than built in parallel: Word proves the pipeline, then Teams, then PDF.

| Application | Order | Text surfaces | Expected UIA access | Known risk |
| --- | --- | --- | --- | --- |
| Microsoft Word (desktop) | 1 | Document body, comments | Full `TextPattern` | Lowest risk |
| Microsoft Teams (new desktop) | 2 | Chat and channel messages, compose box, live meeting captions | **Confirmed 2026-10-14:** `TextPattern` present on some message nodes, absent on others (notably `fui-ChatMyMessage`, which exposes the text only via `Name`). Buttons and labels are `Name`-only as expected. | The accessibility tree built on first query with **no** `--force-renderer-accessibility` needed. Remaining risks are text-quality, not availability: scans bleed across message boundaries, `rects` is sometimes empty, and embedded objects appear as U+FFFC |
| PDF viewer (**Edge** — reference target) | 3 | Page text | **Confirmed 2026-10-14:** `TextPattern` available on page text with bounding rectangles (1–3 per span). Acrobat Reader is not installed on the dev machine, so Edge is the v1 reference viewer. | Confirmed: some PDFs emit **doubled glyphs** (「指指導導教教員員」 for 「指導教員」), which breaks matching. Scanned PDFs still have no text layer |

## Functional requirements

The Must rows define v1: Shift-hover lookup with automatic language detection in Word, Teams and PDFs, with a non-intrusive popup.

| ID | Area | Requirement | Priority |
| --- | --- | --- | --- |
| FR-01 | Trigger | Holding Shift activates scanning; the modifier key is configurable. | Must |
| FR-02 | Trigger | The keyboard hook observes Shift but never consumes it, so typing and Shift+click are unaffected. **Clarified after field use:** not consuming Shift is not sufficient — holding Shift to type a capital fired lookups wherever the mouse was parked. Scanning is suppressed while any non-modifier key is down and for 400 ms afterwards (one `GetKeyboardState` call per loop). The stationary re-probe used for nested lookup is exempt, since it only runs while the cursor is over the popup, where typing does not happen. | Must |
| FR-03 | Trigger | A scan runs only after the cursor rests for a configurable delay (default 50 ms). | Must |
| FR-04 | Capture | Get the text range at the cursor with `RangeFromPoint`, then extend forward up to N characters (default 16), stopping at the end of the line. **For non-Japanese spans, re-range with `TextUnit.Word` first**: forward-only extension clips the word whenever the cursor lands mid-word ("construcción" captures as "onstrucción"). Forward-only remains correct for Japanese, which is what Yomitan does. | Must |
| FR-05 | Capture | **Walk up to five ancestors for a `TextPattern` before falling back.** On Chromium content the element under the cursor is usually a styled span or Group with no text pattern while the text sits on an ancestor; without the walk nearly all web content took the degraded path and yielded one word. Only then fall back to the element's `Name` and pick the word from the cursor's position in its bounding box. **Promoted to Must:** measured on Teams, sent messages (`fui-ChatMyMessage`) expose no `TextPattern` at all and carry their text only in `Name`. Without this fallback a large share of Teams is unreadable. | Must |
| FR-06 | Capture | Return the screen rectangles of the matched text so it can be highlighted, sized to `maxOriginalTextLength` from the lookup. | Should |
| FR-07 | Capture | Per-app allow/deny list by process name. **Note:** Teams text belongs to the child process `msedgewebview2.exe`, not `ms-teams.exe` — any allow-list keyed on the latter would match nothing. Resolve the owning top-level window rather than trusting the process of the element under the cursor. | Should |
| FR-08 | Lookup | Query `/termEntries` for matched length and entry identity, then `/ankiFields` for display HTML. Render progressively: the popup appears on the `/termEntries` result and is enriched when `/ankiFields` lands. Cap `maxEntries` at 3. | Must |
| FR-09 | Lookup | Manual language override from the tray or a hotkey, overriding the automatic choice for the current app until cleared. | Should |
| FR-10 | Lookup | **Choose the language automatically** from the captured text and query the matching Yomitan profile. See *Language detection* below. | Must |
| FR-11 | Popup | Show the popup next to the matched text, kept fully on screen, flipping above or left near edges. **Validate the anchor:** `GetBoundingRectangles` may return several rectangles and next to an inline image the first can be the image's box, empty or implausibly large. Prefer the rectangle containing the cursor, reject zero-area / over-120px-tall / off-monitor ones, and fall back to a caret-sized box at the cursor. Taking index 0 unchecked is what sent the popup to the screen edge. | Must |
| FR-12 | Popup | Render readings, definitions, frequency and pitch data in a WebView2 page. A fast first paint from the `/termEntries` JSON, replaced in place by the `/ankiFields` markers with Yomitan's dictionary CSS applied and `dictionaryMedia` inlined. | Must |
| FR-13 | Popup | Draw a highlight over the matched word in the source app. | Should |
| FR-14 | Popup | Close on Esc, on **any keystroke** (starting to type means the user has moved on from reading, and the popup would otherwise sit over what they are now writing — modifiers excluded, so holding the scan key does not self-dismiss), on a click elsewhere, or when the cursor leaves both the word and the popup. A click *inside* the popup must not dismiss: that is how Back is operated. | Must |
| FR-15 | Popup | Shift-hover inside the popup looks up words in the definitions (nested lookup). **Implemented** in-page rather than through UIA: the popup's own script uses `caretRangeFromPoint` for exact offsets and posts to the host, reusing the FR-04 forward-scan rule so nested lookups behave like top-level ones. Navigation replaces content in place with a back stack, rather than stacking windows as Yomitan does. | Could |
| FR-16 | Popup | Add the word to Anki through AnkiConnect, with the source sentence. Cheaper than it looks: `/ankiCardFormats` and `/ankiFields` already do the rendering. | Could |
| FR-17 | System | Tray icon with on/off toggle, Yomitan API status, detected language and settings. | Must |
| FR-18 | System | Settings for modifier key, scan delay, scan length, popup size, font, theme and the language-to-profile mapping. | Should |
| FR-19 | System | Optional start with Windows. | Could |
| FR-15b | Capture | A mouse selection of up to 100 characters is looked up as a single term, so two-word concepts work. Read via `TextPattern.GetSelection()`; honoured only when the cursor is inside the selection, so a stale selection elsewhere cannot hijack lookups. Longer selections are ignored — that is a translation request, not a dictionary query. | Should |
| FR-19c | Capture | A cursor resting on whitespace produces no lookup. `RangeFromPoint` snaps to the nearest character, so pointing at a gap previously captured the word to its right. | Must |
| FR-19b | Capture | Sanitise the captured span before lookup: cut at the first newline (message boundaries in Teams otherwise bleed into the next message), and strip U+FFFC object-replacement characters emitted for avatars and inline images. | Must |
| FR-20 | System | Fail honestly. The tray and the popup distinguish *API offline* (naming which link in the chain is down), *no text under the cursor*, and *no dictionary match*. Never fail silently. | Must |

## Language detection

FR-10 is the headline requirement, and the part most likely to be got wrong. Detection runs over the **whole captured span**, not the single word under the cursor, because one word rarely carries enough signal.

1. **Japanese** — if the span contains kana or Han characters, it is Japanese. Unambiguous, and no further work is needed.
2. **English vs Spanish** — both are Latin script, so they cannot be reliably separated by inspecting a single word. Spanish-only characters (ñ, á, é, í, ó, ú, ü, ¿, ¡) are a strong signal when present, but too sparse to depend on. The primary mechanism is therefore **disambiguation by lookup**: query **only** the English and Spanish profiles (never Japanese, which returns romaji-converted nonsense for Latin input) and rank the results by match quality — `maxOriginalTextLength` first, then `score`, then exact-match count. The language that produces the better match wins.
3. **Tie-break on the span, not the word.** Measured 2026-10-14: the minimal pair resolves correctly — "construcción" → Spanish, "construction" → English. But short function words that exist in both dictionaries with equal match length do not: "La" in a Spanish sentence resolved to English, because the diacritic hint found nothing in the surrounding span. Score the **whole captured span** for language-specific stopwords (es: *la, el, es, una, que, de*; en: *the, is, a, of, and*) and use that before falling back to diacritics.
4. **Never depend on the browser's active profile.** Every lookup passes `profileIndex` explicitly. Yomitan's own settings page distinguishes the *editing* profile from the *default* profile, which is an easy thing to get wrong by hand; the tool sidesteps it entirely.
5. **Stickiness** — remember the last language chosen per source process and use it to break ties, so the popup does not flicker between languages while reading one document.

Step 2 costs an extra round trip against a serializing server, so its latency must be measured against NFR-01 rather than assumed. FR-09 exists as the escape hatch when detection picks wrong.

## Non-functional requirements

The popup must feel as fast as Yomitan in a browser and must never interfere with the app being read.

| ID | Requirement | Target |
| --- | --- | --- |
| NFR-01 | Latency, cursor rest to popup shown (warm), measured across the **full chain** — UIA read, HTTP, native messaging, extension lookup, render — not the UIA call alone | ≤ 150 ms p95 in Word; ≤ 300 ms p95 in Teams. The lookup leg alone is measured at 34 ms p95 (`/termEntries`) and 58 ms p95 (`/ankiFields`, `maxEntries: 1`); the remaining budget for UIA read and render is therefore tight, which is why rendering is progressive |
| NFR-02 | Latency, first lookup in a session (cold accessibility tree) | ≤ 1 s |
| NFR-03 | Focus: the popup never takes keyboard focus or activates its window (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`) | Teams compose box and meeting controls keep focus in 100% of tests |
| NFR-04 | DPI: process is per-monitor DPI aware (v2) | Correct word hit at 100%, 125%, 150% scaling and across mixed-DPI monitors |
| NFR-05 | Responsiveness: UIA calls run on a background STA COM thread with a timeout; the UI thread never blocks | UIA timeout 500 ms; no UI freeze when the target app hangs |
| NFR-06 | Privacy: captured text goes only to `127.0.0.1`; captured text is not logged by default. Yomitan itself may fetch audio from remote sources, so the hover path must not request `audio` markers | No outbound network traffic from the tool |
| NFR-07 | Resilience: recovers when Teams, Word or the browser restarts | Reconnects without restarting the tool |
| NFR-08 | Footprint while idle | < 1% CPU, < 250 MB RAM excluding the browser — raised from 150 MB to cover WebView2, which costs roughly 40–80 MB |
| NFR-09 | Install without admin rights | Per-user install |
| NFR-10 | Every API call has a timeout (default 300 ms) and lookups are single-flight: a newer hover cancels the in-flight request, because the API host serializes and a queue would compound latency | No lookup backlog; a hung host degrades to a timeout state, not a freeze |

## Architecture

&#91;embedded content: architecture · 4 components, 2 external\]

The hook and scan controller decide when to look, the UIA reader pulls about 20 characters at the cursor, the language detector picks the Yomitan profile, Yomitan returns the entries and rendered HTML, and the popup draws them over the app without taking focus. UIA calls run on a background STA thread (NFR-05) and lookups are single-flight (NFR-10).

## Out of scope and future work

v1 excludes everything that needs OCR or a non-Windows OS.

- **Out of scope:** games, images, video, DRM content, web versions of Teams or Word, macOS and Linux.
- **Future — OCR fallback for scanned PDFs:** when no text layer exists, OCR a small crop around the cursor and send the result through the same lookup path.
- **Future — standalone lookup engine:** remove the browser dependency by running Yomitan's dictionary database and deinflection outside the extension. Before building this, evaluate [Lexiglance](https://github.com/MattFor/lexiglance) (MIT, C++), which already does system-wide UIA and OCR lookup over Yomitan-format dictionaries with no browser dependency. It supports Japanese, Russian, Ukrainian, Korean and Greek but not English or Spanish, so it is not a substitute for v1 — but it is prior art worth reading for UIA technique, and it reframes this item from *build* to *evaluate*.
- **Future — macOS port:** `AXUIElement` with the range-for-position attribute replaces UIA; the rest of the design carries over.

## Acceptance criteria and test plan

v1 is done when every box below is ticked; the first one gates all implementation work.

- [x] **Spike harness built (2026-10-14):** `spike/RosettaSpike`, .NET 10 + FlaUI.UIA3, per-monitor DPI v2. `RosettaSpike api` passes all backend checks; `RosettaSpike hover` performs the UIA capture test.
- [x] **Feasibility spike — Word (2026-10-14):** `TextPattern` confirmed on the Word document body; capture, matched length and bounding rectangles all correct.
- [x] **Feasibility spike — Teams (2026-10-14):** `TextPattern` confirmed on chat message content with no renderer flag required. Japanese (今度 → こんど) and English both resolve. Partial coverage: sent messages need the `Name` fallback.
- [x] **Feasibility spike — PDF (2026-10-14):** `TextPattern` confirmed in Edge's PDF viewer on a Japanese text-layer PDF, with bounding rectangles. Long compounds match correctly (公益財団法人, 6 characters).
- [ ] **Teams text quality:** scans stop at message boundaries, highlight rectangles present, no U+FFFC leakage.
- [ ] **PDF text quality:** doubled-glyph PDFs either match correctly or fail visibly. Some PDFs double every character in the text layer; a collapse heuristic is risky in Japanese (時時, 々) so this may be better surfaced as a "text looks malformed" state (FR-20) than silently repaired.
- [x] **Backend limitation confirmed (2026-10-14):** unpatched `termEntries` ignores `profileIndex` — indices 0/1/2 return identical results. `ankiCardFormats` honours it. Patch written to `patches/termEntries-profileIndex.patch`.
- [x] **Backend patch applied and verified (2026-10-14):** patched Yomitan 26.9.8.0 built from tag and side-loaded into Edge. `termEntries` now honours `profileIndex` and rejects invalid indices. Verified: `running` → *running* (`wty-en-en`) at index 1; `corriendo` → *corriendo* (`wty-es-es`) at index 2; 食べる → 食べる (Jitendex) at index 0. The build reuses the store extension's `key`, so the extension ID is unchanged — dictionaries, profiles and the existing native-messaging registration were all preserved.
- [~] **Automatic language detection:** working in a mixed document — 構わない → かまわない (ja), "construction" → en, "construcción" → es, with no manual switching. Two gaps remain: short ambiguous words still mis-resolve ("La" → en), and the Wiktionary dictionaries return the surface form rather than the lemma, so "running" → *running* rather than *run*. Confirm whether the gloss names the lemma, or add a lemma-bearing dictionary.
- [ ] **Detection latency:** the English/Spanish disambiguation path still meets NFR-01; it must not double the lookup time. Budget from measurements: two `/termEntries` calls = ~68 ms p95.
- [ ] **Teams coverage:** lookup works on a received message, a sent message, text in the compose box and live captions.
- [ ] **Focus:** typing a full sentence in the Teams compose box while the popup is open loses no keystrokes (NFR-03).
- [ ] **PDFs:** lookup works on a text-layer PDF; a scanned PDF shows a "no text here" state instead of failing silently (FR-20).
- [x] **Latency — Word (2026-10-14):** 29 hovers, p95 59 ms against the 150 ms budget. Still to repeat at n=100 and for Teams and PDF.
- [ ] **Scaling:** correct word hit at 150% scaling and on a second monitor at a different scale (NFR-04).
- [ ] **Failure handling:** with the browser closed, the tray names the broken link in the chain; killing Teams mid-lookup does not crash the tool.

## Open questions

- [ ] Will the `profileIndex` patch be accepted upstream, or does v1 ship against a side-loaded Yomitan build?
- [x] ~~Does `/ankiFields` meet the latency budget?~~ **Answered:** 58 ms p95 at `maxEntries: 1`, too slow to sit on the critical path alone, fast enough as a progressive second paint. It cannot supply matched length, so `/termEntries` is needed either way.
- [x] ~~Standard PDF viewer for testing?~~ **Answered:** Edge. It exposes `TextPattern` on PDF page text, and Acrobat Reader is not installed on the dev machine.
- [ ] Popup behavior after Shift is released: stay open until the cursor leaves (Yomitan default) or close immediately? Decide from use, not in advance.
