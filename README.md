# Tango

A system-wide pop-up dictionary for Windows. Hold Shift, hover a word in a native desktop app, and see a Yomitan-style dictionary popup — the same experience Yomitan gives you in the browser.

First targets: Microsoft Word (desktop), Microsoft Teams (new desktop client) and PDF viewers. Japanese, English and Spanish work from day one, and the language is picked automatically from the text under the cursor.

> **Status:** requirements stage. No implementation yet — see [requirements.md](requirements.md).

## How it works

1. A keyboard hook watches for Shift (it never consumes the key).
2. After the cursor rests briefly, the tool reads about 20 characters at the cursor through Windows UI Automation.
3. A language detector picks the matching Yomitan profile.
4. The local Yomitan API returns the entries and rendered HTML.
5. A non-focus-stealing WebView2 popup shows the result next to the word.

Tango reads real on-screen text. It is not an OCR tool for games, images or video.

## Requirements

- Windows 10/11, x64
- A supported browser running with [Yomitan](https://github.com/yomidevs/yomitan) installed
- Three Yomitan profiles: Japanese, English and Spanish
- Python on PATH, with the native-messaging host from [yomitan-api](https://github.com/yomidevs/yomitan-api) registered
- "Enable Yomitan API" turned on in Yomitan's General settings
- A Yomitan build with the `profileIndex` patch for `termEntries` (see below)

## Patched backend

Upstream `termEntries` always uses the active browser profile, so it can't be told to look a word up in a specific language. The patch in [`patches/termEntries-profileIndex.patch`](patches/termEntries-profileIndex.patch) adds an optional `profileIndex` parameter, mirroring the existing `ankiCardFormats` behaviour. Omitting it keeps the current behaviour.

Check that it's loaded (browser running, API enabled):

```
curl -s -X POST http://127.0.0.1:19633/termEntries -d '{"term":"corriendo","profileIndex":2}'
```

This should resolve to "correr" against a Spanish profile and return nothing against the Japanese one.

## Repository layout

| Path | Contents |
| --- | --- |
| `requirements.md` | Full requirements, architecture, acceptance criteria and open questions |
| `patches/` | Upstream patch for Yomitan's API |

## Roadmap

Word first, then Teams, then PDF. Later ideas include an OCR fallback for scanned PDFs, a browser-free lookup engine, and a macOS port.
