# Pull request text — ready to file against yomidevs/yomitan

Base: `master` (verified against `e7334a9`; both hunks still apply at the same lines)

---

## Title

```
Add optional profileIndex to termEntries and ankiFields API paths
```

## Body

```markdown
### What

Adds an optional `profileIndex` to the `termEntries` and `ankiFields` API paths, so an
external client can choose which profile a lookup resolves against. Omitting it keeps
the current behaviour exactly — both fall back to `optionsFull.profileCurrent`.

### Why

Both paths currently hardcode the active profile:

```js
optionsContext: {index: optionsFull.profileCurrent}
```

Since a profile's `language` setting drives deinflection and dictionary selection, an
API client cannot look a term up in any language other than whichever profile the user
last selected in the browser. For a multi-language client this makes the API
single-language in practice: a profile set to Japanese will not deinflect "running" into
"run", and with `enableWanakana` on it does not return *nothing* for Latin input — it
romaji-converts it and returns confident but wrong entries, so a client cannot even
detect the mismatch from an empty result.

`ankiFields` needs the same parameter and for a sharper reason: a client that resolves a
term with `termEntries` against one profile and then renders it with `ankiFields` gets a
glossary from a *different* profile. In practice this shows the correct definition and
then replaces it a moment later with one from the wrong language.

### Precedent

This mirrors `ankiCardFormats` in the same `switch` block, which already accepts an
optional `profileIndex` and resolves it the same way, so the request shape and the
fallback stay consistent across paths.

### Changes

- `ext/js/comm/yomitan-api.js` — `termEntries` and `ankiFields` accept `profileIndex`,
  validate it against `optionsFull.profiles`, and fall back to `profileCurrent`
- `types/ext/yomitan-api.d.ts` — `profileIndex?: number` on both input types
- `docs/api_paths/*.md` in `yomidevs/yomitan-api` — documented separately

### Testing

- `npx eslint ext/js/comm/yomitan-api.js` — clean
- `npx tsc --noEmit --project jsconfig.json` — clean
- `npx vitest run` — 4837 passing

Verified by hand against a build with three profiles (Japanese, English, Spanish):
`termEntries` with `profileIndex` 0/1/2 returns per-language results for the same input,
and an out-of-range index is rejected. Before the change all three returned identical
Japanese results.

### Note

`kanjiEntries` has the same hardcoded profile and would take the identical fix. It is
left out here only because the scope stays minimal and I have no client exercising it —
happy to include it if you would prefer one consistent change.
```

---

## Filing it

`gh` is not installed on this machine, and `tools/yomitan` is a shallow clone of the
release tag rather than a fork, so neither is ready to push a branch.

Two options:

**A. Install the GitHub CLI**, then it can be done end to end from here:

```
winget install GitHub.cli --source winget --accept-package-agreements --accept-source-agreements
gh auth login
```

**B. File it by hand.** Fork `yomidevs/yomitan` on GitHub, then:

```
git clone https://github.com/<you>/yomitan.git
cd yomitan
git checkout -b api-profile-index
git apply /path/to/patches/profileIndex.patch   # or re-apply the two hunks by hand
npx eslint ext/js/comm/yomitan-api.js && npx tsc --noEmit --project jsconfig.json
git commit -am "Add optional profileIndex to termEntries and ankiFields API paths"
git push -u origin api-profile-index
```

Then open the PR with the title and body above.

Their `CONTRIBUTING.md` asks that large features be discussed in an issue first. This is
~10 lines across two endpoints with existing precedent in the same file, so filing the PR
directly is reasonable — but if the maintainers would rather scope it as an API design
question, an issue linking to this PR is the fallback.
