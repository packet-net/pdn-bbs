# LinBPQ/BPQMail → pdn-bbs mailbox importer

**Tool:** `tools/Bbs.Import.Bpq` (the `bpq-import` CLI, linking `Bbs.Core`).
**Runbook:** `scripts/migrate-gb7rdg.sh`.
**Status:** built + tested against the consistent oracle fixture (`docker/oracle/state`) and the real-but-stale GB7RDG snapshot.

This tool migrates a LinBPQ/BPQMail mailbox — its messages, the BID dedup database, and the per-partner forwarding state — into a pdn-bbs SQLite `bbs.db`, preserving the packet network's **no-duplicate-transfer** guarantees. It exists because GB7RDG is moving from a live LinBPQ node to a new pdn+pdn-bbs node, and there is **no rollback** once the new node is live as GB7RDG: if the import is wrong, the new node re-floods the network with duplicate mail.

## Why this is correctness-critical

A BBS does not re-send a message it has already forwarded, and does not accept a message whose BID it has already seen. BPQMail records both facts on disk:

- **BID dedup** — `WFBID.SYS` holds every BID seen in the last ~60 days (the `BidLifetime`). A message whose BID is in this store is rejected (a bulletin always; a personal unless no live copy remains). This is the primary loop/duplicate guard.
- **Per-partner forwarding bitmaps** — each message header carries two bitmaps, `forw[]` (already forwarded to partner *n*) and `fbbs[]` (still queued to partner *n*), where *n* is a partner's `BBSNumber`.

If the new node starts with an empty BID store, or with messages re-queued to partners BPQ already sent them to, it will re-flood. The importer's whole job is to carry both facts over **exactly**.

## The deterministic-rebuild design (repeatability / rollback)

`bbs.db` is built as a **pure function of the BPQ dump**: each run reads the dump and writes a complete, fresh `bbs.db` from scratch. The BPQ source is opened **read-only and never modified**.

Consequences (all of which Tom asked for):

- **Idempotent / re-runnable** — run it as many times as you like for dry runs, incremental pre-syncs, and the final cutover. Same input ⇒ byte-equivalent output (an integration test asserts two runs produce identical row content).
- **Rollback-safe by construction** — "rollback" is just *rebuild or discard the generated `bbs.db`*. There is nothing to undo on the source.
- **Refuses to clobber** — by default the importer will not overwrite an existing target (`--force` overrides, deleting the old DB + its WAL/SHM first). The safe primary model is "write a NEW `bbs.db`".

**Trade-off vs an additive/merge mode.** An additive mode (open an existing `bbs.db`, dedup on BID, insert only what's new) was considered. The rebuild model was chosen as the safe primary because: (a) it has no dependence on prior `bbs.db` state, so a partial/failed run can never leave a half-merged database; (b) it is trivially verifiable — the validation summary describes the *entire* output, not a delta; and (c) the cutover is a one-shot event, not a continuous sync, so incrementality buys little. The importer is written transactionally (a single SQLite transaction; integrity + FK checks pass on the result), so a failed run leaves no target at all rather than a corrupt one.

## The three rules (implemented exactly)

### Rule 1 — import every BID verbatim

- Each imported message's `bid` is set to BPQ's **original** BID (upper-cased and truncated to 12 chars, exactly as both BPQ's `bid[13]` field and pdn's `Message.MaxBidLength` do — no other alteration; any BID that *needed* truncating is counted and warned). A BID is **never** auto-generated for an imported message.
- The pdn `bids` table is seeded from **every** record in `WFBID.SYS`, including "orphan" BIDs whose message has been killed/expired but is still within `BidLifetime`. In the real GB7RDG snapshot this is **1,662 BIDs, of which ~1,470 are orphans** — dropping them would re-open ~1,470 bulletins to re-flooding. Live-message BIDs additionally carry a back-link to their message number; orphan BIDs have `message_number = NULL`.
- Dedup is case-insensitive: the `bids` primary key is `COLLATE NOCASE`, mirroring BPQ's `_stricmp` (`LookupBID`, BBSUtilities.c:1783).

### Rule 2 — pre-mark already-forwarded and still-queued legs

For each message, both bitmaps are decoded with BPQ's exact bit math (`check_fwd_bit`, BBSUtilities.c:2407):

> bit *n* → byte `(n-1)/8`, mask `1 << ((n-1) % 8)`; *n* is a partner's `BBSNumber`.

- A partner whose **`forw`** bit is set → a `forwards` row with **`forwarded_utc` stamped** (already sent).
- A partner whose **`fbbs`** bit is set → a `forwards` row with **`forwarded_utc` NULL** (still queued).
- `BBSNumber → partner-call` comes from the `linmail.cfg` `BBSUsers` records (the `F_BBS` ones). A bit with no matching partner is **dropped with a warning** (a mis-map = wrong/duplicate sends, so it is surfaced, not silently guessed). A bit pointing at our own callsign (BPQ keeps a self-entry) is ignored — mail is never queued to self.

### Rule 3 — carry the message-number high-water mark

BPQ's latest message number is the control record's `length` field (`LatestMsg = MsgHddrPtr[0]->length`, BBSUtilities.c:1393); the next message gets `++LatestMsg`. The importer sets pdn's `sqlite_sequence` for `messages` to `max(control.length, max-imported-number)`, so the next locally-originated message gets a number **above** anything already on the network — a new `<n>_GB7RDG` BID can never reuse a number a partner has already seen.

## Forwarding-partner selection (the F_BBS filter)

`linmail.cfg`'s `BBSForwarding` section can list more entries than the BBS actually forwards to: disabled stubs, and **legacy entries that reuse a `BBSNumber` slot already owned by a real BBS**. The importer therefore imports a partner **only if it has a BBS-checked (`F_BBS`, flags `& 0x10`) user record** in `BBSUsers` — the user flag is the authority, mirroring how the bitmap decode (Rule 2) resolves a slot. This drops two classes of entry:

- **Disabled stubs** — `Enabled = 0`, no real BBS user (the GB7RDG live data had 7, all `BBSNumber` 160 sentinels).
- **BBSNumber-slot collisions** — an active partner reusing a slot owned by the F_BBS BBS. In the GB7RDG data `GB7MNK` and `GB7BRK` (both `Enabled = 1`, real connect scripts) reuse slot 7, already owned by the F_BBS `GB7BPQ`. Keeping them would re-flood those BBSes: the bitmap decode attributes slot 7 to `GB7BPQ` alone, so they would import with **zero** pre-marked legs and the new node would re-send all eligible mail to them.

The kept set is **verified against BPQ's own UI**: the live GB7RDG dump filters to exactly the 15 partners BPQ's "forwarding partners" list shows (16 incl. the GB7RDG self-entry, which is dropped — mail is never queued to self). Every **skipped** partner is reported in the import summary with its enabled/disabled state, so a dropped *active* partner is never silent (this is a no-rollback migration). The filter lives in one shared helper (`PartitionPartners`) used by both the write path and the dry-run projection, so the two can never disagree.

## Source formats (verified byte-for-byte against `bpqmail.h` + the real fixtures)

| File | Format | Notes |
|---|---|---|
| `DIRMES.SYS` | array of `struct MsgInfo`, **308 bytes**, `pack(1)`, LE | record[0] is a control record (its `length`@6 = latest msg number). Legacy `struct OldMsgInfo` (**243 bytes** on 64-bit) auto-detected by record-size divisibility + the control `status==1` marker. |
| `WFBID.SYS` | array of `BIDRec`, **18 or 22 bytes** | size is build-dependent (the union holds a pointer): **18** on a 32-bit LinBPQ (GB7RDG), **22** on a 64-bit one (the docker oracle). Auto-detected. record[0] control holds the count. |
| `linmail.cfg` | libconfig text | `main` (BBSName / SYSOPCall / H-Route), `BBSForwarding.<CALL>` partners, `BBSUsers` (`^`-delimited), `Housekeeping` (BidLifetime / MaxMsgno / MaxAge). |
| `bpq32.cfg` | flat keyword | NODECALL + the `APPLICATION ... ,BBS, ... ,<Call>,<Alias>,...` line; cross-checked against `linmail.cfg` BBSName. |
| `Mail/m_%06d.mes` | raw text | the message body (incl. `R:` lines; a full B2 header for B2 messages). Stored verbatim as a BLOB. |

### `struct MsgInfo` field offsets (308-byte layout)

| Offset | Field | Type | → pdn |
|---|---|---|---|
| 0 | type | char `B`/`P`/`T` (`N`→`T`) | `messages.type` |
| 1 | status | char `N Y F K H D $` | `messages.status` |
| 2 | number | int32 LE | `messages.number` (preserved verbatim) |
| 6 | length | int32 LE | advisory (not stored) |
| 14 | bbsfrom | char[7] | `messages.received_from`, `bids.first_seen_from` |
| 21 | via (@BBS/HA) | char[41] | `messages.at_bbs` |
| 62 | from | char[7] | `messages.from_call` |
| 69 | to | char[7] | `recipients.to_call` |
| 76 | bid | char[13] | `messages.bid` (verbatim) |
| 89 | title | char[61] | `messages.subject` (≤60) |
| 154 | B2Flags | UCHAR | bit 0 (`B2Msg`) noted; body kept as-is |
| 163 | fbbs[20] | bitmap | → queued `forwards` (Rule 2) |
| 183 | forw[20] | bitmap | → sent `forwards` (Rule 2) |
| 247 | datereceived | int64 LE unix | `bids.first_seen_utc`, forward `queued_utc` |
| 255 | datecreated | int64 LE unix | `messages.created_utc` |
| 263 | datechanged | int64 LE unix | `messages.killed_utc` / read time / forward `forwarded_utc` |

> **Important:** `type` and `status` are stored on disk as **ASCII characters**, not the numeric `MSGTYPE_*`/`MSGSTATUS_*` enum values that appear in `bpqmail.h`. The struct fields are `char type; char status;`, and the fixtures confirm it (record 1 of the oracle DIRMES begins `50 4B` = `'P','K'`).

### `BBSUsers` caret-field order (verified vs `GetUserDatabase`, BBSUtilities.c:687–740)

`Name^Address^HomeBBS^QRA^pass^ZIP^CMSPass^lastmsg^flags^PageLen^BBSNumber^RMSSSIDBits^WebSeqNo^TimeLastConnected^Total^LastStats`

0-based field indices: **flags = 8** (`0x10` = `F_BBS` = a forwarding partner), **BBSNumber = 10** (the bitmap bit index). (The task brief said "field 11"; the source confirms 10 — a 0-based off-by-one. The importer uses 10.)

## Field mapping summary

| BPQ | pdn `bbs.db` |
|---|---|
| message type B/P/T (N→T) | `messages.type` (`P`/`B`/`T`) |
| status N/Y/$/F/K/H/D | `messages.status` (verbatim letter) |
| number | `messages.number` (verbatim; high-water → `sqlite_sequence`) |
| from / to / @BBS | `messages.from_call` / `recipients.to_call` / `messages.at_bbs` |
| title | `messages.subject` |
| `m_*.mes` body | `messages.body` (BLOB, verbatim) |
| bid | `messages.bid` + a `bids` row (verbatim) |
| every `WFBID.SYS` BID | a `bids` row (orphans → `message_number NULL`) |
| `forw[]` / `fbbs[]` bit | `forwards` row, sent / queued |
| `BBSForwarding.<CALL>` | `partners` row (script, TO/AT/HR, B2, interval, timeout, HA) |
| `BBSUsers` human records | `users` row (+ a white-pages row when directory fields exist) |

## The password caveat

BPQMail stores BBS passwords as a plaintext/legacy hash in the `BBSUsers` `pass` field; pdn-bbs stores **Argon2id** PHC strings in `mail_auth` (keyed by base callsign). **The two are not convertible**, so passwords cannot migrate. Policy chosen:

- **No `mail_auth` row is written for any imported user.** In pdn that is the natural *disabled* state — `VerifyMailPassword` returns false for a callsign with no row, so IMAP/webmail logins are closed until the sysop sets a password. There is no placeholder/null hash (the column is `NOT NULL` and the public setter always writes a real Argon2id hash).
- Each user must (re)set their BBS/IMAP password on the new node (sysop `SetMailPassword`, ≥8 chars). The user *identity* (callsign, name, home BBS) and directory info are migrated; only the secret is not.

## Known gaps / things to verify before cutover

- **NTS (`T`) traffic** is imported with status preserved (incl. `D` delivered) but no NTS-specific routing state beyond the forward bitmaps.
- **Attachments / B2 parts** are kept inside the body BLOB verbatim (the `B2Msg` flag is detected); they are not split into pdn's `attachments` table. Bodies round-trip losslessly, so this is presentation-only and can be reprocessed later.
- **White pages** are seeded only from `BBSUsers` directory fields (name/home/QRA/ZIP) as authoritative `U` records; BPQ's separate `WP.cfg` directory is **not** consumed (it is a large external dataset; out of scope for the mailbox cutover).
- **Duplicate `BBSNumber`** in `BBSUsers` is handled last-wins with a warning — verify the partner mapping if you see that warning (a wrong map = wrong/duplicate sends).
- **Orphan headers** (a header whose `m_*.mes` body is gone) are imported with an empty body and warned; the cutover runbook **refuses** a dump with any orphan headers, because that means the dump was taken while BPQMail was still running. Always take the authoritative cutover dump with BPQMail **stopped**.
- **Orphan bodies** (`m_*.mes` with no header — already-purged messages) are ignored by design; importing them would resurrect deleted mail. The GB7RDG snapshot has 221 of these.

## Usage

```
bpq-import --source <bpq-dump-dir> --target <bbs.db> [--force] [--own-call CALL]
bpq-import --source <bpq-dump-dir> --dry-run        # validate + print the summary, write nothing
```

The dry run prints the full validation summary: message counts by type/status, BID counts (incl. orphans), per-partner queued/sent leg counts, the high-water mark, and a source-vs-imported diff. Review it before any cutover. See `scripts/migrate-gb7rdg.sh` for the gated cutover runbook (`--dry-run` / `--pre-sync` / `--cutover`).
