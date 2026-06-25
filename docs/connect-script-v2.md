# Connect scripts v2 — structured steps

**Status:** implemented (2026-06-25). Supersedes the flat `EXPECT=SEND` string form (compat spec §4.4 as it was implemented in `ConnectScript.cs`). The store, runner, config/YAML round-trip, the forwarding editor's step UI, and the BPQ importer are all on the structured `ConnectStep` model.

Read alongside [`forwarding.md`](forwarding.md) — this design lives inside its ethos: §4.4 (connect-script semantics), the "warts" list (esp. wart 1 "typed YAML that explains" and wart 4 "scripts aren't write-only"), and the "vocabulary: the jargon goes away" table. It is the connect-script-shaped instance of all three.

## Why

A connect script walks a forwarding cycle through intermediate node hops before the FBB session starts. Today a script is `ConnectScript: IReadOnlyList<string>`, and each post-connect line is parsed by splitting on the **first `=`** and trimming both sides (`src/Bbs.Host/Forwarding/ConnectScript.cs` `ParseStep`). That one decision overloads a single character — `=` — to mean "delimiter", and couples three independent things (what to wait for, what to send, the only punctuation we have) into one string. It creates dead-ends:

1. **`=` cannot appear in an expect** — it is the field delimiter. A node prompt of `=> ` (three bytes: `0x3D 0x3E 0x20` — equals, greater-than, space) *starts* with the delimiter, so it is unrepresentable, escaped or not. This is the concrete report that triggered the redesign: an intermediate URONode hop whose prompt is `=> `.
2. **Leading/trailing spaces cannot appear** in either side — `.Trim()` eats them. Even if the `=` problem were solved, the trailing space of `=> ` is gone.
3. **No control bytes, no per-step options** — no Ctrl-Z, no per-step timeout, no case-sensitivity choice, no "wait for A *or* B". The line *is* the data; there is nowhere to hang a modifier.

The fix is not a cleverer string mini-language (an escape syntax was designed and rejected — see "Rejected" below). It is to retire the flat string and model a step as structured data, where "wait for" and "send" are separate fields and the authoring surface (YAML, or the form UI) carries arbitrary bytes natively.

## Decisions (Tom, 2026-06-25)

- **Structured-only, no legacy converter.** The flat `EXPECT=SEND` string form is removed and understood nowhere — there is no flat→structured auto-upgrade. A legacy store blob (or any non-JSON `connect_script` value) reads as a **blank** script, and the BPQ importer imports a **blank** script. The sysop authors a structured script in the forwarding editor before enabling the partner (a blank script is inbound-only, so it never dials regardless). This is deliberate (Tom, 2026-06-25): "a reasonable ask to get node operators to convert their scripts." See "Migration".
- **One key.** We keep the existing `connectScript:` key name; its items are now structured step maps exclusively. The simple `connect: <call>` shorthand stays for the no-navigation case.
- **regex in v1**, behind `match: regex` (default `substring`).
- **UI in the same pass.** The Forms-tab editor gets a small-JS structured step editor; the test-connect transcript becomes interactive; the YAML tab round-trips the structured maps. See "UI".

## The model

A connect script is a sequence of **steps**. There are two kinds.

### `open` — the dial

The one hop *we* make: the RHP `open` to the first node. Exactly one, and it is the only connect we perform — every later hop is a remote node connecting on our behalf, so foreign node-command dialects (`C 3 !GB7WEM-7`, `CONNECT`, `NC`, …) live untouched inside a later step's `send` string and we never parse them.

```yaml
- open: GB7RDG                 # shorthand: target only
# or
- open: { target: GB7RDG, port: 3 }   # explicit node port for the RHP open
```

A script with no `open` step is INBOUND-ONLY (the partner dials us; we never dial it) — the same meaning `ConnectPlan.IsInboundOnly` carries today.

### expect/send — the workhorse

```yaml
- expect: "GB7RDG}"            # substring to wait for; opaque bytes; quotes carry spaces and '='
  send:   "C 3 !GB7WEM-7"      # text to send after the match (the remote node's own dialect)
- expect: "=> "               # the URONode prompt — the space and the '=' are just characters
  send:   "BBS"
  timeoutSeconds: 30           # optional per-step override of conTimeoutSeconds
```

Fields:

| field | default | meaning |
|---|---|---|
| `expect` | *(absent ⇒ don't wait)* | substring to wait for; opaque bytes |
| `send` | *(absent ⇒ don't send)* | text to send after the match |
| `timeoutSeconds` | `conTimeoutSeconds` | per-step wait override (seconds) — wart 4 |
| `match` | `substring` | `substring` \| `exact-line` \| `regex` |
| `ignoreCase` | `true` | matches today's case-insensitive scan; set `false` for case-sensitive |
| `eol` | `cr` | send terminator: `cr` \| `lf` \| `crlf` \| `none` |
| `raw` | `false` | interpret `\r \n \t \xNN \\` escapes in `send` (e.g. Ctrl-Z = `\x1a`) |
| `name` | *(none)* | label shown in the transcript / health surface so a failure names the step |
| `expectAny` | *(none)* | list of alternatives; first to appear wins (e.g. a prompt *or* a "try later" banner) |

`expect` empty/absent ⇒ send-only (today's bare line). `send` empty/absent ⇒ expect-only (wait, don't send). The failure-marker scan (`BUSY`/`FAILURE`/… `ConnectScriptRunner.FailureMarkers`) and the post-script FBB-SID wait are unchanged.

### Worked example

The transcript that motivated this — RHP-dial GB7RDG, type GB7RDG's own connect command to reach GB7WEM-7 (a URONode), hit URONode's `=> ` prompt, enter the BBS:

```
M9YYY> c gb7rdg
Connecting to GB7RDG on gb7rdg...
Connected to GB7RDG.
Welcome to GB7RDG. ... Type ? for Help
C 3 !GB7WEM-7
READNG:GB7RDG} Connected to GB7WEM-7
URONode v2.15 - Welcome to GB7WEM-1
URONode GB7WEM in IO91un Help: ? <command>
=>
```

becomes:

```yaml
- call: GB7WEM
  connectScript:
    - open: GB7RDG
    - expect: "GB7RDG}"
      send:   "C 3 !GB7WEM-7"
    - expect: "=> "
      send:   "BBS"
```

The `=> ` problem dissolves: in structured YAML it is a quoted scalar. No delimiter collision, because expect and send are separate keys, not two halves of one string. The trailing space is inside the quotes.

### The `connect:` shorthand

`connect: GB7RDG` (a bare call, no navigation) stays as the convenience form — equivalent to a one-step `connectScript: [{ open: GB7RDG }]`. When both are set, `connectScript:` wins (unchanged precedence).

## Rejected: an escape mechanism for the flat form

An earlier draft kept the flat `EXPECT=SEND` string and added backslash escapes processed before the split: `\=` (literal `=`, not the delimiter), `\s` (a space that survives trimming), `\\`, `\t`, `\r`, `\n`, `\xNN`. The `=> ` case would have been authored as `\=>\s = BBS`. It works and it is ugly, and keeping it means perpetuating the overloaded-`=` model we set out to retire. Decision (Tom, 2026-06-25): drop the flat form entirely rather than patch it. Recorded here so the option is not re-proposed.

## Migration

The flat form is retired everywhere, with **no auto-upgrade** — a legacy script becomes blank and the operator rebuilds it. This is safe because:

- **Store reads** (`ConnectScriptJson.Deserialize`): the `connect_script` column now holds a JSON array of steps; any non-JSON value (a legacy newline-joined blob) reads as an empty list. An upgraded node's existing partners come up inbound-only until a structured script is authored — they never dial with a stale/misread script.
- **The BPQ importer** writes a **blank** `connect_script` and imports every partner **disabled** (its existing controlled-cutover default). So a freshly-migrated node dials no one until the sysop authors each script and enables the partner via test-connect. (The old `BpqConnectScript.Translate` line-normaliser is removed — there is no flat script to normalise.)
- **YAML / config parse**: `connectScript:` binds a sequence of step maps. A legacy flat `connectScript:` (a sequence of scalar strings) is not the structured shape; it is not silently honoured. Because `bbs.yaml` partners are a first-boot **seed only** (the store is the source of truth once populated), a live node is unaffected.

Net: nothing dials on a misread script; the cost is that operators re-author connect scripts once, in the new editor.

## Runner changes (`ConnectScriptRunner`)

Additive — the buffer (`ScriptLineBuffer`), the failure scan, and the SID wait are unchanged.

- **`ExpectSendStep`** grows from `(Expect, Send)` to carry `Timeout?`, `Match`, `IgnoreCase`, `Eol`, `Raw`, `Name`, `ExpectAny`.
- **Per-step timeout** overrides the partner `responseWait` when set.
- **`match`/`ignoreCase`**: `TryConsumeThrough` gains exact-line and regex variants alongside today's case-insensitive substring; `ignoreCase` toggles the comparison.
- **`eol`/`raw` on send**: the terminator is selectable (default CR, as today); `raw` runs `send` through C-style escape expansion so control bytes are expressible.
- **`expectAny`**: wait for the first of several substrings; failure markers still abort first.
- **`name`** is threaded into the transcript (`> …` / `< (matched "…")`) and the failure message, so a failed cycle names the step (wart 4).

## UI

The forwarding editor (`src/Bbs.Host/Web/Webmail.cs`, server-rendered, near-zero-JS) keeps its **Forms | YAML** switch over the store. Both surfaces read/write the same structured steps.

### Forms tab — the structured step editor (small JS island)

Replaces the freeform `<textarea name="connectScript">` (`Webmail.cs` `PartnerForm`). A `Dial` block (`open` target + optional port) over a list of step cards:

```
Dial   Target [GB7RDG]   Port [3]      ← the one hop we make

Steps                                   [+ Add step]
┌ 1 ──────────────────────── [↑][↓][✕]
│ Wait for [GB7RDG}        ] (substring ▾) [□ case-sensitive]
│ Send     [C 3 !GB7WEM-7  ]
│ ▸ Advanced — timeout · line-ending · regex · raw bytes · name · "or wait for…"
├ 2 ──────────────────────── [↑][↓][✕]
│ Wait for [=>␣           ]            ← trailing space rendered as a visible ␣
│ Send     [BBS           ]
```

Two UI moves carry the design:

- **Expect and send are separate inputs** — the `=` collision cannot happen.
- **Invisible whitespace is made visible** — leading/trailing spaces in the Wait-for field render as a faded `␣`. This is the answer to "how do I set a trailing space": you don't escape it, you *see* it.

Add/remove/reorder happen client-side and the whole step list submits as one hidden structured field (a small JS island, consistent with the existing test-connect `fetch`). The common case stays two fields; `timeout`/`eol`/`raw`/`name`/`expectAny`/`match=regex` hide behind **Advanced** (progressive disclosure). Choosing `regex` reveals a one-line tester (paste a sample line, see if it matches) and validates the pattern on save.

### Interactive test-connect transcript

The test-connect probe (`POST /forwarding/test-connect` → `ForwardingTester`) already returns the dialogue transcript + observed prompt as JSON. Each `< saw "…"` line in the rendered transcript gets a **"Use as Wait-for"** button that drops the observed bytes straight into a step's `expect` — trailing space and all. The cutover loop becomes: Test connect → see the peer's real `=> ` prompt → click → it is a step, verbatim, with no hand-transcribing of invisible whitespace. This is the biggest usability win and is nearly free — the bytes are already in the response.

### Other surfaces

- **Partner card summary** (`Webmail.cs`): render steps readably — `dial GB7RDG ▸ wait "GB7RDG}" → "C 3 !GB7WEM-7" ▸ wait "=> " → "BBS"` — instead of the `·`-joined raw lines.
- **YAML tab**: unchanged as a surface; now round-trips structured maps. Emit is canonical (always the map form for v2 steps).
- **Help text**: rewritten off the `EXPECT=SEND`/`=`-split language.
- **Validation**, surfaced inline (wart 1): bad regex → "step 3: not a valid pattern"; unknown `eol` → explains the allowed set; an `open` after step 1 → explains there is one dial.

## Scope

**v1 (this design):** `open` step; expect/send with `timeoutSeconds`/`match`/`ignoreCase`/`eol`/`raw`/`name`/`expectAny`; structured-only parse; legacy reads-as-blank (no auto-upgrade); canonical map emit; the Forms-tab step editor, interactive test-connect transcript, and rewritten help/validation.

**Deferred — the model leaves room:**
- Branching / labels / goto (conditional walks). The step list is ordered today; a future `goto`/`when` would extend the step record, not replace it.
- A **named-prompt library** — `expect: { node: uronode }` resolving to the known prompt, since prompts are not standardised across BPQ/URONode/XRouter/NetRom. The genuinely "fit for the future" extension; out of v1.

**Out of scope:** reviving timed `PAUSE` — deliberately superseded by expect-then-send prompt gating (unchanged from today).

## Touch list

- `src/Bbs.Core/Partner.cs` — `ConnectScript` becomes structured steps (or a parallel structured property with the string list kept only as a migration input).
- `src/Bbs.Host/Forwarding/ConnectScript.cs` — structured `ConnectScriptStep` records + fields; `Resolve` reads structured directly; legacy directive handling moves into the migration/upgrade path.
- `src/Bbs.Host/Forwarding/ConnectScriptRunner.cs` — honour the new fields (above).
- `src/Bbs.Host/Web/PartnerYaml.cs` + `BbsHostConfig.cs` `PartnerConfig` — polymorphic-free structured parse/emit; auto-upgrade legacy input; YamlDotNet binding for the step maps.
- `src/Bbs.Host/Web/Webmail.cs` — the Forms-tab step editor, interactive transcript, card summary, help, validation.
- `docs/forwarding.md` §4.4 + the `bbs.yaml` generated-config comment block — document the structured form; mark the flat form retired.
- Tests: `tests/Bbs.Host.Tests/ConnectScript*Tests.cs` — re-pin against the structured model + the migration.

## Open follow-ups

- Confirm whether `Partner.ConnectScript` becomes the structured type outright or a structured property sits beside the retained string list (migration-input only). Outright is cleaner; the parallel property is a softer landing for any other reader of the store.
- The named-prompt library (deferred) wants its own small design pass and a curated prompt set.
