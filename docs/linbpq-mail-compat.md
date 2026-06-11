# LinBPQ Mail (BPQMail) Compatibility Specification

**Target:** a ground-up .NET BBS for the pdn packet node whose hard requirement is full interoperability with LinBPQ's built-in mail system (BPQMail) — as a bidirectional forwarding partner and as an RF-facing BBS with the classic terse command surface.

**Status:** research-complete spec, 2026-06-11. Every wire-level claim below is sourced; points where sources conflict or are silent are tagged **[VERIFY-ORACLE]** and collected in §9.

**Primary sources** (precedence order for conflicts: LinBPQ source > official FBB protocol docs > BPQ official docs > community docs):

| Tag | Source |
|---|---|
| [BPQ-SRC] | `github.com/g8bpq/linbpq` @ 6.0.25.30 (Apr 2026). Key files: `BBSUtilities.c`, `FBBRoutines.c`, `MBLRoutines.c`, `MailRouting.c`, `WPRoutines.c`, `lzhuf32.c`, `MailDataDefs.c`, `bpqmail.h`, `LinBPQ.c`, `MailCommands.c`. (Note: the author's GitHub account is **g8bpq**, not john-wiseman.) |
| [FBB-PROTO] | `f6fbb.org/protocole.html` (official FBB protocol page) |
| [FBB-APP9] | FBB documentation Appendix 9, "Forward protocol" (`docfwpro.htm`) — adds the `F> HH` checksum and the R/E/H responses |
| [FBB-APP10] | FBB documentation Appendix 10, "Compressed forward" (`docfwcom.htm`) |
| [FBB-SID] | `f6fbb.org/.../sid.html` — official SID feature-letter table |
| [IW3FQG] | IW3FQG mirror of the FBB forward-protocol text (older revision; has the B1 resume `Offset+6` wording) |
| [WL-B2F] | winlink.org "Open B2F — Winlink Message Structure and B2 Forwarding Protocol" (rev. Feb 2018) |
| [F4HOF] | f4hof.fr B2F ABNF grammar page |
| [LZHUF1] | `lzhuf_1.c` as shipped with FBB / vendored in paclink-unix (the canonical FBB compressor); `stock_lzhuf.c` = original Yoshizaki for diffing |
| [BPQ-DOC] | cantab.net/users/john.wiseman/Documents/ — `MailServer.html`, `BBSUserCommands.html`, `MailServerConfiguration.html`, `Forwarding.html`, `NTSFacilities.html`, `HintsandKinks.html`, `InstallingLINBPQ.html`, `BPQCFGFile.html`, `ImportExport.html`, `BBSChangeLog.html`, `InfoandHelp.html`, `WebMail.html`, `BBSBeaconSupport.html` |
| [M0LTE-IT] | `github.com/m0lte/linbpq` `tests/integration/` — a *proven* scripted FBB peer (`helpers/fbb_partner.py`), a full `linmail.cfg` generator (`helpers/bpqmail_cfg.py`), and two-instance forwarding tests against the real binary |
| [LOCAL] | `/home/tf/packet.net/docker/compose.interop.yml`, `/home/tf/packet.net/docker/linbpq/bpq32.cfg` |

Keyword usage: **MUST/SHOULD/MAY** as RFC 2119; the MUST/SHOULD/LATER product partition is §8.

---

## 0. Scope and architecture notes

- BPQMail is **in-process** in LinBPQ: "LINBPQ is a Linux version of the BPQ32 Node, BBS and Chat Server components" [BPQ-DOC InstallingLINBPQ]. There is no separate daemon. It is enabled by `./linbpq mail` or a `LINMAIL` line in `bpq32.cfg`.
- LinBPQ ≈ BPQ32 (Windows) for everything in this spec; the BBS code is shared. Differences are config-file names and management UI only.
- BPQMail speaks, partner-configurable: **MBL/RLI plain text**, **FBB blocked compressed B / B1 / B1F**, and **B2F** — over AX.25, NET/ROM, and raw TCP (FBBPORT). It does **not** any longer speak FBB *unblocked* ASCII to non-BPQ partners (§3.13.1) — this materially shapes our MUST line.
- "B1F" is not a distinct protocol: it is the FBB blocked compressed protocol **version 1** (`B1` in the SID) used under FBB basic protocol (`F` in the SID). The community habit of writing "B1F" comes from the SID letter run `...B1FHM$...`.

---

## 1. User command surface (RF users)

### 1.1 Connect / identify

- A user reaches the BBS by connecting (AX.25/NET/ROM) to the BBS application callsign/alias, or by typing the APPLICATION command (canonically `BBS`) at the node prompt.
- First-ever connect: BPQMail auto-creates a user record and prompts `Please enter your Name\r>\r` (configurable `NewUserPrompt`); the next line is stored as the name. If the reply looks like a SID (`[...]`) or `;FW:`, the caller is auto-classified as a temporary BBS [BPQ-SRC, BPQ-DOC changelog 1.0.4.25]. A config option (`Don't Request Name`) skips this.
- Known user: welcome message, then prompt. Default welcome: `Hello $I. Latest Message is $L, Last listed is $Z` (Windows default appends `$N Active Messages` and `Type H for help`). Variables: `$U` callsign, `$I` first name, `$L` latest msg number, `$N` active messages, `$Z` last listed, `$W` CR, `$X` msgs for user, `$x` NEW msgs for user, `$F` msgs queued to forward to this station [BPQ-DOC MailServerConfiguration + BPQ-SRC]. Treat the banner as **opaque, sysop-configurable text**; never parse it.
- If the user has no Home BBS set (and the option isn't disabled): `Please enter your Home BBS using the Home command.\rYou may also enter your QTH and ZIP/Postcode using qth and zip commands.\r` [BPQ-SRC].
- A caller with the **BBS** user flag gets the SID instead of the chatty banner path (§3.2) — this is how forwarding sessions begin.

### 1.2 Prompt

- Default prompt for all user classes: **`de <BBSNAME>>` + CR LF** — `sprintf(Prompt, "de %s>\r\n", BBSName)` [BPQ-SRC BBSUtilities.c ~10491]. Sent after every command completion.
- All three prompts (normal / new-user / expert) are sysop-configurable. The docs claim expert mode "changes the BBS prompt, normally to just >" [BPQ-DOC] but the source default is `de CALL>` for all three — **[VERIFY-ORACLE #6]**.
- Sign-off (`B`, `Bye`, `NODE`): default `73 de <BBSNAME>` + CR, then disconnect / return to node.
- Our BBS: implement `de <CALL>>` as the default prompt shape. Automated peers key on `>`-terminated lines, so any prompt MUST end in `>` and welcome text MUST NOT end in `>` (BPQ actively strips trailing `>` from welcome text to avoid faking a forwarding prompt [BPQ-DOC changelog 6.0.21.1]).

### 1.3 User command set

Verbatim command list from [BPQ-DOC BBSUserCommands] cross-checked against [BPQ-SRC]. Commands are case-insensitive. Our BBS MUST implement the **bold** core; the rest per §8.

| Command | Action / notes |
|---|---|
| **`?` / `H`(elp)** | Send help text. If `help.txt` exists it replaces the built-in text [BPQ-DOC InfoandHelp]. |
| **`A`(bort)** | Abort paged output. Response: `\rOutput aborted\r`. |
| **`B`(ye)** | Sign-off message, disconnect. |
| **`L`** | List messages new since last `L`. `LR` = same, oldest first. |
| **`Lx`** | List by status: `LN LY LH LK LF L$ LD` (LH/LK sysop-only: `LH or LK can only be used by SYSOP\r`). |
| **`LB` / `LP` / `LT`** | List by type (Bulletins / Personals / NTS Traffic). |
| **`LM`** | List messages to me. `LC` = list bulletin TO-field categories with counts. |
| **`LL n`** | List last n messages. `L n-` / `L n-m` = ranges. |
| **`L< call` / `L> call` / `L@ bbs`** | List from / to / via. `L@` matches up to the length of the input string. |
| | Options combine: `LMP`, `LMN`, `LB< G8BPQ`, `LNT` etc. [BPQ-SRC help text]. |
| **`R n [n...]`** | Read message(s) by number. `RM` = read new messages to me; `RMR` = RM oldest-first. Errors: `Message %d not for you\r`, `Message %d not found\r`. |
| **`S[type] call [@ bbs] [< from] [$bid]`** | Send. `S`=`SP` personal; `SB` bulletin; `ST` NTS traffic. `< from` only from BBS-flagged peers (`*** < can only be used by a BBS\r`). Full grammar §1.5. |
| **`SR n`** | Reply to message n (title auto `Re:...`, no title prompt). |
| **`SC n call [@ bbs]`** | Copy message n (title auto `Fwd:...`). |
| **`K n`** | Kill message n (`Message #%d Killed\r` / `Not your message\r` / `Message %d not found\r`). |
| **`KM`** | Kill my **read** personal messages (doc says "haven't yet read" — the source kills status `Y`; doc is a known typo — **[VERIFY-ORACLE #7]**). |
| **`X`** | Toggle expert mode (`Expert Mode\r` / `Expert Mode off\r`). |
| **`N name`** | Set name (source truncates at 17; doc says 12). `Q qth` → `QTH is %s\r`. `Z zip` → `ZIP is %s\r`. |
| **`Home [bbs]` / `HOMEBBS`** | Show/set home BBS; `.` deletes it. Bare-call warning: `Please enter HA with HomeBBS eg g8bpq.gbr.eu - this will help message routing\r`. Reply `HomeBBS is %s\r`. |
| **`I`** | Send sysop `info.txt` (else `SYSOP has not created an INFO file\r`). |
| **`I call`** | WP lookup, wildcards `*CALL`, `CALL*`, `*CALL*`. `I@ bbs`, `IH route`, `IZ zip` = WP queries by home BBS / hierarchical area / ZIP. |
| **`V`** | `BBS Version %s\rNode Version %s\r`. |
| **`OP n`** | Set page length (0 = off; 1–9 rejected `Page Length %d is too short\r`; echo `Page Length is %d\r`). |
| **`NODE`** | Exit BBS back to node. |
| **`D n` / `Delivered n`** | Flag NTS (T) message delivered → status `D`. `Message #%d Flagged as Delivered\r`; non-T → `Message %d not an NTS Message\r`. |
| `PASS pw` | Set BBS password (`BBS Password Set\r`). |
| `CMSPASS pw` | Winlink CMS password (`CMS Password Set\r`). |
| `POLLRMS ...` / `SHOWRMSPOLL` | Winlink polling management. |
| `IDLETIME n` | Session idle timeout, 60–900 s (defaults: 5 min user, 2 min forwarding). |
| `FILES` / `LISTFILES`, `READ name`, `YAPP name` | File area listing / read / YAPP binary download. |
| `PG` | G7TAJ PG-server gateway list. |

Unknown command → `Invalid Command\r`; >4 in a session → `Too many errors - closing\r` + disconnect [BPQ-SRC; BPQ-DOC changelog 6.0.16.1]. Bad option letter → `*** Error: Invalid List option %c\r` (same shape for Kill/Read/Send).

### 1.4 Sysop commands

`AUTH n` (TOTP via BPQAUTH from the BBS password; replies `Ok\r`/`AUTH Failed\r`), `DOHOUSEKEEPING` (`Ok\r`), `REROUTEMSGS` (`Ok\r`), `EXPORT n file` / `IMPORT file`, `EDITUSER`/`EU` (flags `EXC EXP SYSOP BBS PMS EMAIL HOLD RMS APRS`), `KH`, `K< call`, `K> call`, `LH`, `LK`, `UH n|ALL` (`Message #%d Unheld\r`; status reverts to `$` if forwarding queued else `N`), `SETNEXTMESSAGENUMBER n`, `FWD ...` (§4.4). Sysop status: local console / 127.0.0.1 telnet, or `AUTH`. [BPQ-DOC BBSUserCommands; BPQ-SRC]

### 1.5 Message entry flow (exact)

Send-line grammar (BPQ source comment): `SB WANT @ ALLCAN < N6ZFJ $4567_N0ARY` [BPQ-SRC BBSUtilities.c DoSendCommand].

1. Parse `S<type>` (`S`→`SP`; valid types `P B T`, plus internal `R`/`C` for reply/copy). TO required: `*** Error: The 'TO' callsign is missing\r`. Optional in any order after TO: `@ ATBBS` (≤40 chars), `< FROM` (BBS peers only), `$BID`. Bad token → `*** Error: Invalid Format\r`. TO/FROM truncated to 6 chars, SSID stripped ("Remove any (illegal) ssid" [BPQ-SRC]). `call@bbs` without spaces accepted; multiple recipients separated by `;`; `to@route!BBSCALL` = source routing.
2. If no `@`: auto-complete from the recipient's Home BBS (`Address @%s added from HomeBBS\r`) or WP (`Address @%s added from WP\r`).
3. Filters / dup BID checked. Interactive dup-BID refusal: `*** Error- Duplicate BID\r`. (BBS peers get `NO - BID\r` — §3.10.)
4. Title prompt: **`Enter Title (only):\r`**. Empty title cancels: `*** Message Cancelled\r`. Stored max 60 chars. (BBS peers get `OK\r` instead and no text prompt.)
5. Text prompt: **`Enter Message Text (end with /ex or ctrl/z)\r`**.
6. Terminators: a line beginning **Ctrl-Z (0x1A)**, or the line **`/ex`** (case-insensitive), or the AEA-TNC artifact `/E<0x1A>>`. There is **no** body-stage abort command — the only cancel is the empty title [BPQ-SRC ProcessMsgLine]. **[VERIFY-ORACLE #8]**
7. Acceptance: BID auto-allocated as `<msgno>_<BBSNAME>` if none given. Response: **`Message: %d Bid:  %s Size: %d\r`** (note: two spaces after `Bid:`) [BPQ-SRC ~6503], optionally followed by `@BBS specified, but no forwarding info is available - msg may not be delivered\r` or the local-user variant, then the prompt.

### 1.6 List output format

Per-message line [BPQ-SRC ListMessage]:

```
nodeprintf(conn, "%-6d %s %c%c   %5d %-7s@%-6s %-6s %-s\r",
    number, date_ddMMM, type, status, length, to, via_first_element, from, title);
```

i.e. `msg#(6L) dd-mmm TS size(5R) TO(7)@VIA(6) FROM(6) title` where `TS` is the two-letter type+status (e.g. `PN`, `BF`, `P$`). VIA shows only the first dotted element. WebMail shows the header `#  Date  XX  Len  To  @  From  Subject`; whether the terminal listing emits a header row is **[VERIFY-ORACLE #9]**. Empty lists: `No Messages found\r` / `No New Messages\r`.

### 1.7 Paging

Per-user `OP n` page length, persisted. Continue prompts [BPQ-SRC]:
- generic: **`<A>bort, <CR> Continue..>`**
- in listings: **`<A>bort, <R Msg(s)>, <CR> = Continue..>`** (you can type `R nnn` mid-list, then the list resumes)

`A` clears queued output. Paging is **disabled on forwarding sessions** [BPQ-DOC changelog 1.0.4.8] — our forwarding peer never sees a pause prompt.

---

## 2. Message model

### 2.1 Types

`P` personal, `B` bulletin, `T` NTS traffic [BPQ-DOC]. Forwarding priority order T, P, B [BPQ-DOC changelog 1.0.4.25]. T messages are readable by any user and have the `D` delivered status; they route by TO-field longest-prefix wildcard before AT (§4.3).

### 2.2 Statuses

From [BPQ-DOC MailServer], verbatim meanings:

| Status | Meaning |
|---|---|
| `N` | Not read or forwarded |
| `Y` | Has been read |
| `$` | Bulletin that still has stations to be forwarded to |
| `F` | Has been forwarded to all stations |
| `K` | Killed (remains on disk until housekeeping removes it; sysop `LK` sees it) |
| `H` | Held — can't be forwarded, read or killed except by sysop |
| `D` | Delivered (NTS only) |

There is **no archive status** in BPQMail. Transitions: new = `N` (or `H` if held by filters / new-user hold / too-big / looping / bad date; or `$` immediately for a bulletin with queued forwarding). Read by addressee: `N→Y` (never overwrites K/H/F/D; T messages are *not* set Y on read). Forwarded-to-all: `→F` (per-partner bits cleared one at a time; F only when all clear). Kill rights: sysop anything; `P` by sender or addressee; `B` by sender; `T` by anyone (configurable) [BPQ-SRC OkToKillMessage].

### 2.3 BID / MID

- Auto-BID: **`<msgno>_<BBSCALL>`** (e.g. `3331_GM8BPQ`). One namespace — personal messages get the same scheme (the "MID"); the SID `M` letter advertises that personals carry message IDs and are deduped too.
- User/peer-supplied BID: the `$bid` token on the S line; the `$` is a sigil, **not stored**. Stored BID is ≤12 chars (truncated) [BPQ-SRC CreateMessage].
- Dedup: BID database (`WFBID.SYS`), lookup **case-insensitive** (`_stricmp`) [BPQ-SRC LookupBID]. Bulletins: any known BID → reject. Personals: reject only if a live copy (status N/Y/H, same TO) exists; forwarded/killed copies are accepted again [BPQ-SRC DoWeWantIt — comment: "If P Message, dont immediately reject on a Duplicate BID … If not, accept it again"].
- BIDs expire after `BID Lifetime` (default 60 days); a message older than BID Lifetime **or** `MaxAge` (default 30, bulls) on receipt is held [BPQ-DOC MailServerConfiguration; BPQ-SRC].
- B2F MIDs may arrive with `@MPS@R` suffixes — strip them [BPQ-SRC FBBRoutines.c:708].

### 2.4 Addressing

- Full form: `TO @ AT` where AT is a **hierarchical address (HA)**: `CALL[.#REGION].COUNTRY.CONTINENT`, e.g. `G8BPQ.#23.GBR.EU`. Routing "will only work if messages have a valid, full (ie including continent) HA" [BPQ-DOC Forwarding].
- `WW` is the implicit root of every HA. Country codes are understood (`ALL@USA` ≡ `ALL@USA.NA`); 2-char and 4-char continents equivalent (`NA`≡`NOAM`, option `Use 4 Char Continent Codes`). BPQ canonicalises the AT to a WW-rooted element list before matching [BPQ-SRC MailRouting.c].
- TO ≤ 6 chars, callsign-shaped, SSID stripped. AT ≤ 40 chars.
- FROM = connected user's call, or the `< call` override (BBS peers only).
- **Home BBS**: per-user, set by `Home` or harvested via WP; used to auto-complete `@` and pushed into WP.
- Special TO prefixes (BPQ extensions, all LATER for us): `rms:`/`@winlink.org`, `smtp:`, `nts:nnnnn@NTSxx`, `bull/`.

### 2.5 User flags relevant to interop

**BBS** flag = the partner switch: "Allows the BBS to queue messages for forwarding to it, to use compressed (B/B1/B2) forwarding protocols and to accept messages sent on behalf of other users" [BPQ-DOC MailServerConfiguration]. **PMS** = compressed forwarding for non-BBS clients (Winpack-style). Incoming forwarding connects are matched **by exact source callsign including SSID** against a BBS-flagged user record — if it doesn't match, no SID is sent and no forwarding happens [M0LTE-IT test_two_instance_bbs_forwarding.py]. Others: Expert, Excluded, Hold-messages (default ON for new users unless `Don't Hold messages from new users`), NTS MPS, RMS-related.

---

## 3. BBS-to-BBS forwarding — the core requirement

### 3.0 Protocol family map

| Mode | SID needs | Proposals | Transfer encoding | LinBPQ support |
|---|---|---|---|---|
| MBL/RLI text | `$` (no usable `F`) | `S<type> ... $bid` one at a time | plain text, `/ex` or Ctrl-Z | yes (fallback) |
| FBB ASCII basic (unblocked) | `F` | `FB` ×5 | plain text + Ctrl-Z | **effectively no** (§3.13.1) |
| FBB compressed V0 ("B") | `BF` | `FA` ×5 | SOH/STX/EOT + LZHUF, no CRC | yes |
| FBB compressed V1 ("B1"/"B1F") | `B1F` | `FA` ×5 | SOH/STX/EOT + CRC16+LZHUF, resume | yes — **the BBS↔BBS default** |
| B2F (Winlink) | `B2F` | `FC` (mixable with FA/FB) | same framing, B2 message format | yes (BBS↔BBS and RMS) |

Negotiated mode = intersection of the two SIDs gated by per-partner config (`AllowCompressed`/`AllowB1`/`AllowB2`/`AllowBlocked`). "B2 implies B1"; B2 partners get B1-style CRC framing [BPQ-SRC Parse_SID comments].

### 3.1 Session-level flow

1. **Caller** connects (AX.25/NET/ROM/TCP) to the partner's BBS application.
2. **Called** station sends its SID first: "The SID is always sent by the BBS as the first line after the connection" [FBB-SID] — in practice followed by optional text and a `>`-terminated prompt: "I will receive the SID followed by some text and the prompt (>)" [FBB-PROTO].
3. **Caller** replies with its own SID (no prompt needed after it), then immediately sends its first proposal block. The caller MAY precede the SID with `;FW:` lines (Winlink) — and BPQ emits `; WL2K DE <call> (<locator>)` and `; MSGTYPES ...` comment lines after its SID. **Any line starting `;` is a comment and MUST be ignored** unless it's an extension you support [WL-B2F].
4. Proposal block → FS response → message bodies → **direction reverses**: "When the other BBS has received all the messages in a block, it implicitly acknowledges by sending its proposal" [FBB-PROTO].
5. A side with nothing (more) to send sends `FF` (alone — "This line must not to be followed by a F>"). If the other side also has nothing it sends `FQ` and the link is disconnected [FBB-PROTO]. The side receiving FQ disconnects (BPQ answers a final `BYE\r` in some text-mode paths).

### 3.2 SID

Grammar: `[` author `-` version `-` features `]` CR. "A SID is composed of fields (at least two, maximum three) separated by a hyphen" [FBB-SID]. Feature letters, optionally followed by a version digit ("If no digit is given, version 0 is assumed"):

| Letter | Official meaning [FBB-SID] | What LinBPQ actually does with it [BPQ-SRC Parse_SID] |
|---|---|---|
| `A` | Acknowledge for personal messages | ignored |
| `B` | FBB compressed protocol V0 | sets compressed (if `AllowCompressed`); looks ahead for digits |
| `B1` | FBB compressed protocol V1 | sets B1 (if `AllowB1 || AllowB2`) |
| `B2` | (Winlink ext.) B2F | sets B1+B2 (if `AllowB2`) — "B2 uses B1 mode (crc on front of file)" |
| `F` | FBB basic protocol | sets FBB forwarding (if `AllowBlocked`), clears MBL mode |
| `H` | Hierarchical Location designators | ignored (BPQ always emits it) |
| `I` | (undocumented; BPQ emits it) | ignored — **[VERIFY-ORACLE #15]: semantics unknown; treat as inert** |
| `J` | (Winlink) "not Radio-Only network" | clears Radio-Only mode; absence of J = Radio-Only [BPQ-SRC comment] |
| `L` | (FBB emits e.g. `B1FHLM$`) — undocumented in [FBB-SID] | ignored |
| `M` | Message identifier (MID) supported | ignored (BPQ always emits it) |
| `W` | (BPQ emits `FW` on inbound SIDs) | ignored |
| `X` | Compressed batch forwarding (XFWD) | ignored — do not implement |
| `$` | BID supported — "must be the last character of the list" | sets MBL forwarding + BBS flag (baseline; upgraded by F) |

**What LinBPQ sends.** Format string `"[%s%d.%d.%d.%d-%s%s%s%sIH%sM$]\r"` [BPQ-SRC MailDataDefs.c:113] — `I`, `H`, `M`, `$` are unconditional literals. Two emission sites:

- **Answering an inbound connect** (features = what the per-partner config *allows*): `B` if AllowCompressed, `1` if AllowB1, `2` if AllowB2, `FW` if AllowBlocked, `J` unless Radio-Only. Typical stock partner config → **`[BPQ-6.0.25.30-B12FWIHJM$]`**. (Yes, `FW`, not `F` — the parser only keys on `F`; `W` is decorative.)
- **Replying on an outbound connect** (features = what was *negotiated* from the partner's SID): `B`, `1` only if B1-and-not-B2, `2`, plain `F`, `J` — e.g. **`[BPQ-6.0.25.30-B1FIHJM$]`** against a B1 partner.

**Parsing rules to implement:** scan the feature field (after the last `-`, inside `]`); case per table; ignore unknown letters silently. Additionally LinBPQ sniffs the whole SID string: the substring `BPQ` anywhere → enables BPQ↔BPQ extensions (`; MSGTYPES`, FC extra fields §3.3); `RMS Ex`/`PAT`/`Paclink`/`WL2K-` → Winlink client handling. **Our SID must NOT contain "BPQ"** unless we implement those extensions — pick e.g. `[PDN-0.1.0-B1FHM$]`. **[VERIFY-ORACLE #1]**

**The compression guard (critical):** after SID parsing, if the partner negotiated FBB blocked (`F`) but not compressed (`B`) and is not BPQ, LinBPQ logs `Uncompressed Blocked Forwarding is no longer supported - reconfgure BBS for MBL forwarding` and **disconnects** [BPQ-SRC BBSUtilities.c:9355]. Consequence: *a minimal partner cannot do FBB-ASCII-only — it must implement LZHUF compression (or fall back to MBL text mode).*

### 3.3 Proposal blocks: FA / FB / FC and `F>`

All protocol lines start with `F` in column 1 and end with CR [FBB-PROTO].

**FA/FB — 7 fields, space-separated** [FBB-PROTO; FBB-APP9]:

```
FB P F6FBB FC1GHV FC1MVP 24657_F6FBB 1345
^  ^ ^     ^      ^      ^           ^
|  | from  @BBS   to     BID/MID     size (bytes)
|  type: P | B | T
verb
```

- "ALL the fields are necessary … must hold seven fields. If a field is missing upon receipt, an error message will be sent immediately followed by a disconnection" [FBB-PROTO].
- Verb selection: `FB` = ASCII-mode (or, in compressed mode, "binary compressed file" — never implemented by FBB and not used by BPQ); `FA` = compressed-mode message. LinBPQ emits `FA` when compressed was negotiated, else `FB` [BPQ-SRC FBBRoutines.c:1040]: `"%s %c %s %s %s %s %d\r"`.
- The @BBS field: BPQ sends the message's AT if present, else **the partner's own callsign** [BPQ-SRC].
- **Size = UNCOMPRESSED message size in bytes, *including* the `R:` line the sender will prepend** [BPQ-SRC BBSUtilities.c:6886]. It is advisory; receivers use it only for the MaxRXSize check. **[VERIFY-ORACLE #10]**
- From/To in proposals are ≤6 chars, SSID stripped. A TO >6 chars in a received FA gets a polite `-`, not a protocol error [BPQ-SRC].
- **Block limit: ≤5 proposals**, and BPQ additionally stops when the accumulated size exceeds `MaxFBBBlock` (default 10000 — the FBB INIT.SRV default "10KB for VHF use" [FBB-PROTO]).

**FC — B2F proposal** [WL-B2F; F4HOF]:

```
FC EM 12345_K4CJX 1306 281 0
^  ^  ^           ^    ^   ^
|  |  MID (≤12)   usize csize trailing 0 (implementations send it; the F4HOF ABNF omits it — accept both)
|  type: EM = encapsulated message, CM = control message
```

LinBPQ to a non-BPQ partner: `"FC EM %s %d %d %d\r"` (trailing 0); to a BPQ partner it appends `from at to type` extension fields [BPQ-SRC FBBRoutines.c:987]. No sender/recipient in the standard FC — they live in the B2 header. FA/FB/FC may be intermixed in one block [WL-B2F].

**`F>` — end of proposal block, with checksum:**

- Grammar: `F>` optionally followed by space + 2 hex digits: "F> HH … HH is optional. It is the checksum of the whole proposal in hexadecimal" [FBB-APP9].
- Algorithm: sum every byte of every proposal line **including each terminating CR** into an 8-bit accumulator; transmit its two's complement: `cksum = (-sum) & 0xFF`. Receiver adds the received byte to its own sum; result must be 0 mod 256, else `*** Proposal Checksum Error` + disconnect [BPQ-SRC FBBRoutines.c:796,1049; M0LTE-IT fbb_partner.py send_proposal_block].
- LinBPQ **always sends** the checksum (`"F> %02X\r"`); on receive it is **optional** (validated only when present). Our BBS MUST send it and MUST validate when present.
- Worked example (real): `FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ 123_GB7PDN 456` + CR sums to 0x69 ⇒ line is `F> 97`.

### 3.4 FS responses

One character (or `!`/`A`+digits group) per proposal, in order: "FS line MUST have as many +,-,=,R,E,H signs as lines in the proposal" [FBB-APP9]. Sent as `FS <chars>\r`.

| Char | Official meaning [FBB-PROTO/APP9/IW3FQG] | LinBPQ emits? | LinBPQ accepts (as proposer)? [BPQ-SRC FBBRoutines.c:309–474] |
|---|---|---|---|
| `+` / `Y` | yes, send it | `+` for FA/FB, `Y` for FC | yes → sends message |
| `-` / `N` | no, already have it | `-` (dup BID, filtered, oversize) | yes → marks forwarded-elsewhere, P→status F |
| `=` / `L` | later / defer ("already receiving it") | `=` only when the BID is mid-transfer on another session | yes → `Defered = 4` (skip next 4 cycles) |
| `H` | accepted but will be held (V1) | never | treated as `+` (send) — except from RMS Express, where it means defer ("FBB uses H for HOLD, but I've never seen it. RMS Express sends H for Defer" [BPQ-SRC comment]) |
| `R` | rejected (V1; sender should NOT mark forwarded — FBB: "The message is not marked as 'F', and still can be forwarded to another BBS" [FBB-APP9]) | never | treated like `-` |
| `E` | error in the proposal line (V1) | never | logged "Proposal %d Rejected by far end", then like `-` |
| `!offset` / `Aoffset` | yes, send from byte *offset* of the **compressed** payload (V1 resume) | `!%d` when it holds restart data (B1/B2 only) | yes → parses offset, then sends from there |

Notes:
- BPQ-as-receiver answers, per proposal: dup BID → `-`; over `MaxRXSize` → `-`; `RefuseBulls` and type B → `-`; BID currently in transit → `=`; restartable partial → `!<n>`; else `+`/`Y`.
- Anything else in an FS → BPQ: `*** Protocol Error - Invalid Proposal Response'` + disconnect.
- If all our proposals were rejected and the partner is a Winlink client, BPQ sends the `FF` itself [BPQ-SRC:467]. Normal flow: after FS, the proposer transmits the accepted messages immediately, in proposal order, with **no per-message framing between FS and the first byte**.

### 3.5 Message transfer — ASCII basic mode

For completeness (you will likely never speak it to LinBPQ — §3.13.1): each accepted message is sent as *title line* CR, *body*, then **Ctrl-Z (0x1A)** on the last line; "There is no blank line between the messages" [FBB-PROTO]. After the last accepted message, the direction reverses (the receiver proposes or sends FF).

### 3.6 Message transfer — binary blocked mode (B / B1 / B2)

Per accepted FA/FC proposal, the sender streams, with **no acknowledgement between blocks** ("Unlike YAPP transfers, there is no individual packet acknowledgement" [FBB-PROTO]):

**1. SOH header block:**

```
0x01  <len>  <title bytes…> 0x00  <offset ASCII…> 0x00
```

- `len` = byte count of everything after it (title + NUL + offset + NUL). Title ≤80 bytes (FBB), BPQ truncates >60 on receive. "French regulations require that the title … transmitted in readable ascii and not compressed" [FBB-PROTO].
- Offset = ASCII decimal, "1 to 6 bytes". V0: "always equal to zero". V1: the resume offset granted via `!offset`. LinBPQ sends `"%6d"` (space-padded, so len = title+8) in B1 and `"%06d"` (zero-padded) in B2 [BPQ-SRC SendCompressed/SendCompressedB2]; it *parses* leniently (`atoi` after the title's NUL). We SHOULD send `0` or the granted offset, any width 1–6; we MUST parse any of these forms.

**2. STX data blocks**, repeated:

```
0x02  <size>  <data…>
```

`size` = 1..255, or **0x00 meaning 256** [FBB-PROTO; BPQ-SRC:1187]. LinBPQ emits 250-byte blocks (B1) / 256-byte blocks with size 0x00 (B2). Receivers MUST accept any block size 1–256.

**3. EOT trailer:**

```
0x04  <checksum>
```

`checksum` = two's complement of the 8-bit sum of **all STX-block payload bytes** (not the SOH header, not the STX/size framing bytes): "the sum of all the data bytes of the transmitted file, modulo 256 … and then two's complemented"; verification: payload-sum + checksum ≡ 0 (mod 256) [FBB-PROTO; BPQ-SRC:1263]. On mismatch: discard the message, send `*** Erreur checksum` (FBB) / BPQ logs `*** Message Checksum Error`, and disconnect. **[VERIFY-ORACLE #11]** (confirm BPQ's checksum coverage excludes the SOH header bytes — the source sums only data-block payloads).

After the EOT of the last accepted message, the receiver takes its turn (proposals or FF).

### 3.7 The LZHUF payload (what's inside the STX blocks)

The concatenated STX payloads form one compressed object per message:

| Mode | Payload layout |
|---|---|
| **B** (V0) | `[4-byte LE uncompressed length][LZHUF bitstream]` |
| **B1 / B2** (V1) | `[2-byte LE CRC-16][4-byte LE uncompressed length][LZHUF bitstream]` |

- "The LZHUF_1 program used with option e1 generates … CRC16: 2 bytes, Length: 4 bytes, Datas: rest of the file" [IW3FQG]. "In case of forwarding with a BBS using version 0, only the part from offset 2 will be sent" — i.e. B = the same object minus the CRC.
- **CRC-16**: table-driven XMODEM/CCITT — polynomial 0x1021, init 0, `crc = (crc << 8) ^ crctab[(byte ^ (crc >> 8)) & 0xFF]`, table "calculated by Mark G. Mendel" [LZHUF1:61–97; BPQ-SRC lzhuf32.c]. Computed over **everything after the CRC itself**: the 4 length bytes + the whole compressed bitstream ("computed for the full binary file including the length of the uncompressed file (4 bytes in top of file)" [IW3FQG]). Stored little-endian (`out[0]=crc&0xff; out[1]=crc>>8`).
- **LZHUF algorithm**: Yoshizaki/Okumura LZHUF (LZSS + adaptive Huffman) with FBB's parameters — **N = 2048** (ring-buffer window; stock lzhuf.c uses 4096 — this is the critical delta), **F = 60** (lookahead), **THRESHOLD = 2**, `NIL = N`, `N_CHAR = 256 − THRESHOLD + F = 314`, ring buffer **pre-filled with 0x20 (space)** for positions 0..N−F−1 [LZHUF1:106–109,669; BPQ-SRC lzhuf32.c]. Bitstream details (match-position encoding tables, adaptive-Huffman update with MAX_FREQ 0x8000 reconstruction) are byte-identical to stock lzhuf.c apart from N. **Practical instruction: port `lzhuf_1.c` (paclink-unix) or `lzhuf32.c` (LinBPQ) verbatim rather than re-deriving** — both are vendored at `/tmp/paclink-unix/lzhuf_1.c` and `/tmp/linbpq-src/lzhuf32.c`, and wl2k-go has a clean-room Go port (`wl2k-go/lzhuf`) to crib tests from.
- The compressed plaintext for an FA message = `R:` line(s) + (blank line if first hop) + body — i.e. the **subject travels only in the SOH header**, not in the compressed text. For FC, the plaintext is the entire B2 message (§3.9). A stored-B2 message forwarded over FA is flattened to its first `Body:` part [BPQ-SRC].
- LinBPQ supports a degenerate "blocked but uncompressed" copy-through mode (`Compress == 0`) only against BPQ partners — ignore it.

### 3.8 Resume / restart (B1+)

- Granting: a receiver holding `n` bytes of a previously-broken transfer answers that proposal `!n` (offset counts bytes of the **compressed object**, i.e. of [CRC+len+bitstream]). BPQ persists partials in `Mail/Restart/<n>` for 2 days, gives up and re-accepts from scratch after 10 attempts [BPQ-SRC GetRestartData].
- Sending after `!offset`: "the 6 top bytes will be always sent, then seek to Offset+6, then send data" [IW3FQG] — i.e. resend CRC16+length always, then continue from `offset+6`. The receiver validates the resumed whole via the CRC16 ("the only mean to be sure that the part of the already received file matches with the new one").
- BPQ restart quirk: on resume it first emits a special 6-byte STX block carrying the original CRC+length words ("FBB Seems to insert 6 Byte message ... original csum and length" [BPQ-SRC:1300 area]) which is included in the EOT checksum. **[VERIFY-ORACLE #12]**
- Minimal-partner stance: MUST parse `!n`/`An` and honour it when proposing (trivial — you hold the whole compressed object; resend bytes 0–5 then from n+6). MAY never *grant* offsets (always answer `+` and re-receive from scratch); that is fully conformant.

### 3.9 B2F message format

The FC-proposed object, before compression [WL-B2F; F4HOF]:

```
Mid: 12345_K4CJX<CR><LF>
Date: 1999/09/22 14:33<CR><LF>
Type: Private<CR><LF>
From: SMTP:user@example.com<CR><LF>
To: W1AW<CR><LF>
To: W4ABC<CR><LF>
Cc: N8PGR<CR><LF>
Subject: This is a sample address header<CR><LF>
Mbo: SMTP<CR><LF>
Body: 1302<CR><LF>
File: 3556 NOLA.XLS<CR><LF>
File: 5566 NEWBOAT.HOMEPORT.JPG<CR><LF>
<CR><LF>
<body: exactly 1302 bytes><CR><LF>
<attachment 1: exactly 3556 bytes><CR><LF>
<attachment 2: exactly 5566 bytes><CR><LF>
```

Rules: header is US-ASCII, CRLF line endings, case-insensitive field names (but field *values* preserve case); `Mid:` MUST be the first line; `File:` order must match attachment order; unknown fields MUST be ignored; body may not be empty; the blank line separates header from body; `Body:`/`File:` counts exclude the terminating CRLF, which is mandatory and additional. `Type:` ∈ Private | Bulletin | Service | Inquiry | Position Report | Position Request | Option | System. `Mbo:` = originating BBS. Multiple To:/Cc: allowed (this and attachments are *the* point of B2F: "Standard FBB B1 and lower protocols do not support multiple address messages and messages with embedded attachments" [WL-B2F]).

LinBPQ's generated header for a natively-stored message [BPQ-SRC FBBRoutines.c:1816]:

```
MID: %s\r\nDate: %s\r\nType: %s\r\nFrom: %s\r\nTo: %s\r\nSubject: %s\r\nMbo: %s\r\nContent-Type: text/plain\r\nContent-Transfer-Encoding: 8bit\r\nBody: %d\r\n\r\n
```

(Date `YYYY/MM/DD HH:MM`; Type spelled out `Private`/`Bulletin`/`Traffic`.) Inbound FC type field must be 2 chars (`EM`/`CM`); BPQ stores all B2 arrivals as type `P` internally.

Framing: the B2 object is compressed and shipped exactly like B1 (CRC16+len+LZHUF in SOH/STX/EOT blocks; BPQ uses 256-byte blocks and a `%06d` offset here). The Winlink data-flow document's "up to five messages combined into a single file then compressed as a unit" wording conflicts with per-proposal `csize`; in practice (BPQ, wl2k-go) **each message is compressed separately** — **[VERIFY-ORACLE #13]**.

Authentication comment-lines (`;PQ:` challenge, `;PR:` response, `;FW:` pickup list with `call|hash`) are Winlink-only; ignore unless/until we gateway to Winlink.

**Does LinBPQ speak B2F?** Yes — generically, to any partner that negotiates `B2`, including BBS↔BBS; official guidance is "You should only use B2 if the other end insists on it (only RMS, as far as I know)" [BPQ-DOC Forwarding]. So B2F is NOT needed for stock LinBPQ interop; B1 is the lingua franca.

### 3.10 MBL/RLI text forwarding (the no-FBB fallback)

Selected when the partner SID has `$` but FBB wasn't negotiated, or via the `TEXTFORWARDING` script directive. One message at a time:

- Proposal: `"S%c %s @ %s < %s $%s\r"` → e.g. `SB WANT @ USA < W8AAA $1029_N0XYZ` [BPQ-SRC MBLRoutines.c:387].
- Responses BPQ parses: line starting `N` = no; line starting `O` (`OK`) = yes; then it waits for the `>` prompt. As receiver BPQ answers `OK\r`, `NO - BID\r`, `NO - BULLS NOT ACCEPTED\r`, or `NO - REJECTED\r`, each followed by the prompt (`>\r`, or `F>\r` during reverse) [BPQ-SRC].
- Transfer: title line, `R:` line(s), blank line, LF-stripped body, then `\r\x1a` (if `SendCTRLZ`) else `\r/ex\r`.
- Reverse handover in MBL mode is the bare `F>` prompt; `*** DONE` ends.

### 3.11 Reverse forwarding

Built into the block flow: every block boundary reverses the transfer direction (§3.1 step 4). "Reverse poll" in BPQ config merely means *dial even when you have nothing queued* so the partner gets a turn; a caller with nothing to send opens with `FF` instead of a proposal block. Per-partner `DoReverse` defaults TRUE; a `MSGTYPE` script directive without `R` makes BPQ answer a partner's `F>`/turn with `FQ` [BPQ-SRC].

### 3.12 Error handling

- Missing proposal fields → error + disconnection [FBB-PROTO].
- Bad `F>` checksum → `*** Proposal Checksum Error` + disconnect [BPQ-SRC].
- Bad EOT checksum → `*** Erreur checksum` (FBB wording) + disconnect; restart data invalidated [FBB-PROTO; BPQ-SRC].
- Bad FS char → `*** Protocol Error - Invalid Proposal Response'` (sic, trailing quote) + disconnect [BPQ-SRC].
- Timeouts: per-partner `ConTimeout` (default 120 s) on the SID handshake; idle timeout 2 min on forwarding sessions.

### 3.13 LinBPQ-specific behaviours a partner must know

1. **No unblocked-ASCII FBB.** The plain-text-after-FS code path is unreachable in current LinBPQ and non-BPQ partners advertising `F` without `B` are disconnected (§3.2). Compression is effectively mandatory for FBB mode.
2. **Line framing: send CRLF.** "The FBB spec allows bare CR; in practice, linbpq's BPQMail demultiplexes rapidly-arrived bare-CR-separated lines as one buffer and misparses them. Use `\r\n` for reliable framing" [M0LTE-IT fbb_partner.py send_line]. (BPQ's own emissions are CR-terminated; tolerate both on receive, emit CRLF for command lines.)
3. **BPQ extensions** (gated on `BPQ` substring in partner SID — do not trigger them): `; MSGTYPES <spec>` comment line propagating type/size/reverse filters; FC proposals with 4 extra fields (`from at to type`).
4. **OpenBCM telnet escaping**: 0xFF doubled in binary streams when partner SID says OpenBCM — irrelevant unless we run over raw telnet to an OpenBCM.
5. **5-proposal / MaxFBBBlock limits** on its own blocks; it accepts up to 5 in ours (allocates exactly `5 * sizeof(struct FBBHeaderLine)`) — never send more than 5. **[VERIFY-ORACLE #14]**
6. After our `FS`, accepted messages arrive back-to-back; BPQ runs "Winlink Check" content filters on arrival and may hold (status H) without telling us — delivery-assertions in CI should grep status, not just existence.

### 3.14 R: lines and loop prevention

Every hop prepends one `R:` line to the top of the message text (newest first). LinBPQ's exact writer [BPQ-SRC, 4 sites, identical]:

```c
sprintf(Rline, "R:%02d%02d%02d/%02d%02dZ %d@%s.%s %s\r\n",
    yy, mm, dd, hh, min, msgnum, BBSName, HRoute, RlineVer);
```

→ `R:120218/1023Z 8277@G8BPQ.#23.GBR.EU BPQ1.4.48` (documented example, [BPQ-DOC ImportExport]). Fields: UTC date/time of *receipt at that hop*, local message number, `CALL.HIER`, software version (`BPQ6.0.25` / `BPQK…` for KISS builds). A blank line follows the R:-block iff the body didn't already start with `R:` (i.e. the originating hop inserts the separator). Note: classic FBB R: lines use a richer `@:CALL.HIER [QTH] #:num $:BID` form; BPQ neither emits nor requires it, but its WP harvester reads both — our emitted format should be **exactly BPQ's** for maximal parser compatibility. **[VERIFY-ORACLE #4]**

**Loop prevention** in BPQ is: (a) BID dedup (primary), (b) an own-callsign scan of the leading R: lines — `OurCount > 1` → hold "Message may be looping" (one prior transit through self is tolerated), (c) date sanity from the *last* (oldest) R: line: >7 days future → hold; older than BidLifetime/MaxAge → hold; unparseable → hold "Corrupt R: Line - can't determine age" [BPQ-SRC BBSUtilities.c:6230–6347]. There is **no hop-count limit**. Our BBS MUST prepend a correctly-formatted R: line on every forward (BPQ derives message age and WP data from it) and SHOULD mirror the same three loop checks.

### 3.15 Minimal conformant partner (the wire-level MUST)

For stock LinBPQ to forward happily in **both** directions, a partner must:

1. Accept inbound connects on its BBS callsign and send `[<name>-<ver>-B1FHM$]` first; on outbound, wait for the partner SID + `>` prompt, then send its SID.
2. Parse SIDs per §3.2 (letters B/1/2/F/$; ignore the rest; never crash on unknowns).
3. Emit FA proposals (7 fields, ≤5/block, sizes incl. R: line), `F> XX` with checksum; parse FB/FA/FC syntactically (FC may be declined with `-` if B2 unimplemented — but if you don't advertise `2`, BPQ won't send FC).
4. Parse and emit `FS` with at least `+ - =` emission and `+ Y - N = L H R E !n An` acceptance; honour `!n` when proposing.
5. Encode/decode the B1 binary object: SOH(title,offset)/STX(≤256)/EOT(2's-complement payload checksum) framing around `[CRC16-LE][len32-LE][LZHUF N=2048,F=60]`.
6. Prepend a BPQ-shape R: line when forwarding; BID dedup (case-insensitive, ≤12 chars) answering `-` to dups.
7. Speak the turn-taking: block-by-block reversal, `FF` when empty, `FQ` answer + disconnect.
8. Send protocol lines CRLF-terminated; treat `;`-prefixed lines as comments.

Everything else (B0, B2F, MBL, resume-granting, H/R/E emission, X) is negotiable-off via the SID/feature intersection.

### 3.16 Worked transcripts

**(a) Compressed B1 session, we call LinBPQ** (`caller>` = us / `called>` = LinBPQ; `<XX>` = raw byte):

```
called> [BPQ-6.0.25.30-B12FWIHJM$]<CR>
called> Hello PDN. Latest Message is 41, Last listed is 0<CR>
called> de GB7BPQ><CR><LF>
caller> [PDN-0.1.0-B1FHM$]<CR><LF>
caller> FA P M0LTE GB7BPQ.#23.GBR.EURO G8BPQ 123_GB7PDN 456<CR><LF>
caller> F> 97<CR><LF>
called> FS +<CR>
caller> <01><0D>Hello<00>     0<00>            ; SOH: len 13 = 5(title)+8
caller> <02><FA><250 payload bytes>            ; STX block(s): CRC16,len32,lzhuf…
caller> <02><38><56 payload bytes>
caller> <04><C3>                               ; EOT + 2's-comp sum of the 306 payload bytes
called> FA P G8BPQ GB7PDN.#23.GBR.EURO M0LTE 1042_GB7BPQ 312<CR>
called> FB B GB7BPQ GBR PACKET 1043_GB7BPQ 1530<CR>   ; (FB only if compression was NOT negotiated — illustrative)
called> F> 0F<CR>
caller> FS +-<CR><LF>
called> <01>…<00>     0<00> <02>… <04>…        ; message 1 only
caller> FF<CR><LF>                              ; we have nothing more
called> FQ<CR>
        (called disconnects)
```

**(b) ASCII basic exchange (canonical FBB example, abridged)** [FBB-APP9]:

```
caller> [FBB-5.11-FHM$]
caller> FB P F6FBB FC1GHV.FFPC.FRA.EU FC1MVP 24657_F6FBB 1345
caller> FB B F6FBB FRA FBB 22_456_F6FBB 8548
caller> F> HH
called> FS +-
caller> Title 1st message
caller> Text 1st message ......
caller> ^Z
called> FB P FC1GHV F6FBB F6FBB 2734_FC1GHV 234
called> F> HH
caller> FS -
caller> FF
called> FQ
```

**(c) MBL/RLI exchange:**

```
called> [BPQ-6.0.25.30-IHM$]            ; no B/F usable ⇒ MBL
called> de GB7BPQ><CR><LF>
caller> SP G8BPQ @ GB7BPQ < M0LTE $123_GB7PDN
called> OK
called> >
caller> Subject line
caller> R:260611/0930Z 123@GB7PDN.#23.GBR.EURO PDN0.1
caller>
caller> body text…
caller> /ex
called> >
caller> F>                               ; reverse handover
called> *** DONE                         ; (or its own S-proposals)
```

---

## 4. Forwarding configuration semantics (how the oracle decides what to send us)

Knowing BPQMail's routing model is required to configure the oracle and to design our own equivalent.

### 4.1 Per-partner record

"You have to have a BBS record for each station that you forward from or to, whether or not you initiate the connection" [BPQ-DOC Forwarding]. Stored in `linmail.cfg` group `BBSForwarding.<CALL>`; full key table (UI label → cfg key) [BPQ-SRC SetupForwardingStruct; BPQ-DOC; M0LTE-IT]:

| cfg key | Meaning |
|---|---|
| `TOCalls` | TO-field distribution list (exact match; NTS wildcards `123*`, `*`; `!`/`-` prefix = never) |
| `ATCalls` | AT-field list (exact; `*` wildcard) — plus the **implied AT route**: a message AT'd to a partner's own call always matches it |
| `HRoutes` | hierarchical routes for flood bulls |
| `HRoutesP` | hierarchical routes for personals + directed bulls |
| `BBSHA` | the partner's own full HA (used for the flood "in target area" test) |
| `FWDTimes` | UTC time bands `hhmm hhmm` limiting auto-forwarding |
| `ConnectScript` | §4.4 (multi-line stored `|`-joined) |
| `Enabled`, `FwdInterval` | auto-dial on/off; poll interval in **seconds** (default 3600) |
| `RequestReverse`, `RevFWDInterval` | dial even with empty queue, to poll the partner |
| `FWDNewImmediately` | dial on enqueue (~2 s + jitter) |
| `AllowBlocked`, `AllowCompressed`, `UseB1Protocol`, `UseB2Protocol` | the SID feature gates (§3.2). B2 forces Compressed; Blocked ⇔ Compressed for non-BPQ |
| `MaxFBBBlock` | proposal-block byte cap (default 10000; code compares raw byte sums — community "KB" claims are wrong/legacy — **[VERIFY-ORACLE #16]**) |
| `SendCTRLZ` | MBL terminator Ctrl-Z vs `/ex` |
| `FWDPersonalsOnly` | queue only P (and T) to this partner |
| `ConTimeout` | handshake timeout, seconds |

Globals: `MaxTXSize`/`MaxRXSize` (default 99999; bigger inbound → `-`; bigger local → held), `MaxAge` (30), `RefuseBulls`, `SendPtoMultiple`, `WarnNoRoute`, `FWDAliases` (`AMSAT:WW`, `CALIF:CA.USA` — interpretation-only aliasing), FBB-style filter lines (reject/hold by from/to/at/bid/len).

### 4.2 Routing decision

[BPQ-DOC Forwarding] (doc headline: "forwarding is handled rather differently from other BBS software") + [BPQ-SRC MailRouting.c]:

- Bulls that have **reached their target area** = "Flood Bulls"; not yet = "Directed Bulls", routed like personals. Unconvertible addresses are treated as flood.
- **P + directed B → exactly ONE partner**: first TO-list match, else implied-AT, else exact AT-list, else the partner with the **most matching HR elements** (`HRoutesP`), else wildcard AT. "So if you define BBS1 with HR EU and BBS2 with HR GBR.EU, a message for G8BPQ@G8BPQ.#23.GBR.EU will be sent to BBS2 … There is no need for an exclusion rule." (`SendPtoMultiple` relaxes to all best-depth partners.)
- **Flood B → every partner** that (a) is *in the target area* — the message HA must lie within the partner's `BBSHA` — AND (b) matches an `HRoutes` entry in **all** its elements ("If it only wants local messages … put the required level - eg GBR.EU wouldn't get @EU or @WW messages").
- **T (NTS)**: longest TO-prefix wildcard match wins; "It will only route on the AT field if there are no matches on TO" [BPQ-DOC NTSFacilities].
- Never to the partner it came from; never to self (unless `ForwardToMe`); no match → "Routing Trace - No Match" (+ optional sysop warning).

### 4.3 Receive-side acceptance summary (oracle behaviour to test against)

In order [BPQ-SRC DoWeWantIt]: RefuseBulls? → `-`; size > MaxRXSize → `-`; known BID (B, or live-copy P) → `-`; BID in transit → `=`; partial restart held → `!n`; else `+`/`Y`. After receipt: content filters may hold; R:-line checks may hold (§3.14); messages to servers (WP, REQDIR) are consumed.

### 4.4 Connect scripts & the FWD command

Script lines are sent verbatim to the node ("you don't need to program the node responses - the software knows what to look for" [BPQ-DOC Forwarding]) except directives: `TIMES hhmm hhmm` (sectioning), `ELSE [DELAY n]` (fallback connect path; failure detected by scanning for BUSY/FAILURE/SORRY/INVALID/RETRIED/`ERROR - `/UNABLE TO CONNECT/DISCONNECTED/FAILED TO CONNECT/REJECTED…), `MSGTYPE <spec>` (e.g. `PTRB1000` — types, per-type size caps, `R` = accept reverse), `INTERLOCK n`, `SKIPPROMPT`, `SKIPCON`, `TEXTFORWARDING`, `SETCALLTOSENDER`, `ATTACH`, `RADIO`, `PAUSE n`, `FILE`, `IMPORT`, `RMS`, `SendWL2KFW`. Progress is recognised on `" CONNECTED"`/`OK`/etc.; after the last line BPQ waits for a SID or `>`.

Sysop `FWD` command: `FWD <call>` show; `FWD <call> NOW [elem|elem|...]` immediate cycle with optional inline script; `FWD QUEUE` (`%s %d Msgs\r` rows); `FWD <call> +-EN/RE/SE`; `FWD <call> <interval>` [BPQ-SRC MailCommands.c]. `FWD <call> NOW` is the deterministic CI trigger.

---

## 5. White Pages (WP)

- WP is a callsign→(home BBS, name, QTH, ZIP) directory; it improves under-specified addresses and feeds the `I`/`I@`/`IH`/`IZ` commands. **A partner needs no WP support whatsoever for forwarding to work** — WP updates are ordinary store-and-forward messages addressed `TO WP`, and emission is off by default (`SendWP = 0`).
- **Update message**: From = BBS, To = each configured `TO[@VIA]` (conventionally `WP@<region>`), title **`WP Update`**, type P or B (`SendWPType`), body lines [BPQ-SRC WPRoutines.c:1419]:

```
On YYMMDD CALL/T @ HOMEBBS zip ZIP NAME QTH
On 111120 N4ZKF/I @ N4ZKF.#NFL.FL.USA.NOAM zip 32118 Dave 32955
```

`?` for unknown fields; `/T` record type: `U` user-entered, `G` guessed from R: lines, `I` from R:-line BBS info; absent = `G`. Parser caps: HA≤40, ZIP≤8, Name≤12, QTH≤30, call 3–6.
- Inbound processing is unconditional for any message TO `WP` (body lines starting `On `); P-type WP messages are consumed (killed) after processing; `FilterWPBulls` kills WP bulls. `/U` records override; others go to a temp record promoted after 30 days [BPQ-SRC ProcessWPMsg/UpdateWP].
- Generation cadence: at Housekeeping when `SendWP` (changed records only, ≤5-day window) [BPQ-SRC Housekeeping.c:323].
- BPQ also **harvests WP from R: lines** of transiting mail (`/I` and `/G` guesses) — another reason our R: format must parse cleanly.
- Our stance: SHOULD consume (cheap, useful), MAY emit; never required (§8).

---

## 6. Housekeeping & sysop surface (interop-relevant subset)

- Daily at `Maintenance Time` (+ on demand via `DOHOUSEKEEPING`): first physically remove K-status messages, then kill expired ones [BPQ-DOC HintsandKinks]. Lifetimes (days) per type+state: Personals Read/Unread/Forwarded/Unforwarded, Bulls Forwarded/Unforwarded, NTS Delivered/Forwarded/Unforwarded (all default 30 in current code); per-From/To/At overrides (`ALL, 10` style); BID Lifetime default 60; message renumbering at `Max Message Number`.
- "Send Non-delivery Notifications": a message to the originator of a P/T killed while still status N — partner-visible behaviour (you may receive these as ordinary P messages).
- Interop effects to honour: don't re-propose a BID for `BID Lifetime`; expect `-` on stale bulls (MaxAge); expect held (`H`) messages to be invisible non-sysop.
- No archive function; Linux "recycle" = move to `Deleted/`.

---

## 7. Standing up the oracle (LinBPQ + BPQMail in the CI docker stack)

### 7.1 Today's stack [LOCAL]

`docker/compose.interop.yml` service `linbpq`: image `m0lte/linbpq@sha256:872343ff…` (~6.0.25.23), named volume `linbpq-data:/data` + **read-only bind** of `docker/linbpq/bpq32.cfg`, ports 127.0.0.1: 8008 (HTTP), 8010 (telnet TCPPORT), 8000 (AGW), 8093/udp (AXIP), static IP 172.30.0.10, KISS-TCP dial to netsim 172.30.0.12:8102. **Mail is NOT currently enabled** — no `mail` arg/`LINMAIL`, no APPLICATION line, no linmail.cfg.

### 7.2 Enabling mail

1. **bpq32.cfg** additions (the bind-mounted file):

```
LINMAIL

APPLICATION 1,BBS,,PN0TST-1,PNBBS,255
```

Syntax: `APPLICATION n,CMD,NewCommand,Call,Alias,Quality` [BPQ-DOC BPQCFGFile]; convention is BBS on SSID −1. **Gotcha**: the Call field must be non-empty or inbound L2 SABMs to the BBS call are silently ignored (`APPLCALL[0]` stays zero) [M0LTE-IT]. Keep it appl **1** so BPQMail's default `BBSApplNum = 1` matches. Optionally add `FBBPORT=8011` in the Telnet port block (+ compose port map) for raw-TCP FBB partners — useful for driving protocol tests without AX.25.

2. **linmail.cfg** — libconfig format, lives at `/data/linmail.cfg`, **auto-rewritten by BPQMail** (web saves + housekeeping; via `/dev/shm/linmail.cfg.temp`) so it must be **seeded into the writable volume, not bind-mounted `:ro`** (entrypoint shim: `cp -n /seed/linmail.cfg /data/linmail.cfg`). Proven minimal seed [M0LTE-IT bpqmail_cfg.py, validated against the real binary]:

```
main:
{
  Streams = 10;
  BBSApplNum = 1;
  BBSName = "PN0TST-1";
  SYSOPCall = "PN0TST";
  H-Route = "#23.GBR.EURO";
  EnableUI = 0;
  RefuseBulls = 0;
  DontHoldNewUsers = 1;
  DontCheckFromCall = 1;
  DontNeedHomeBBS = 1;
  DontNeedName = 1;
  AllowAnon = 1;
  MaxTXSize = 99999;
  MaxRXSize = 99999;
  Log_BBS = 1;
  Log_TCP = 1;
};
BBSForwarding:
{
  GB7PDN:
  {
    TOCalls = ""; ATCalls = ""; HRoutes = "WW"; HRoutesP = "GBR.EURO";
    FWDTimes = "";
    ConnectScript = "C 2 GB7PDN";
    Enabled = 1; RequestReverse = 0;
    AllowBlocked = 1; AllowCompressed = 1;
    UseB1Protocol = 1; UseB2Protocol = 0;
    SendCTRLZ = 1; FWDPersonalsOnly = 0; FWDNewImmediately = 1;
    FwdInterval = 2; RevFWDInterval = 0;
    MaxFBBBlock = 10000; ConTimeout = 60;
    BBSHA = "GB7PDN.#23.GBR.EURO";
  };
};
BBSUsers:
{
  ; ^-delimited: Name^Address^HomeBBS^QRA^pass^ZIP^CMSPass^lastmsg^flags^PageLen^BBSNumber^RMSSSIDBits^WebSeqNo^TimeLastConnected^Stats^LastStats
  ; flags 0x10 = F_BBS — REQUIRED, keyed by EXACT source callsign incl. SSID
  GB7PDN = "BBS^^^^^^^0^16^0^1^0^0^0^^";
};
Housekeeping:
{};
```

Notes: multi-line fields are `|`-joined single strings; callsign keys starting with a digit get a `*` prefix; the `BBSUsers` record for our BBS callsign (exact, with SSID, as seen on the inbound connect) with flag bit 0x10 is what makes LinBPQ treat our connect as a forwarding session at all.

3. The image already ships the HTML templates (web Mail Mgmt works at :8008; from 127.0.0.1 no password needed).

### 7.3 Driving it in CI

- **Post mail**: telnet :8010 → `USER`/`password` from the cfg (`admin`/`admin` today) → `BBS` → `SP USER @ GB7PDN` → title → body → `/EX` → expect `Message: nnn` + `Bid:` [M0LTE-IT does exactly this].
- **Trigger forwarding deterministically**: (a) `FwdInterval = 2` + `FWDNewImmediately = 1` (fires ~2 s after enqueue — what the m0lte harness uses); (b) sysop telnet `FWD GB7PDN NOW`; (c) web POST `/Mail/FWDSave?<sessionkey>` body `StartForward`.
- **Force WP emission**: set `SendWP = 1` + `SendWPAddrs`, then `DOHOUSEKEEPING`.
- **Re-run routing after config edits**: `REROUTEMSGS`.
- **Our BBS connects in** over netsim KISS (AX.25 to `PN0TST-1`) or AXIP map, or FBBPORT raw TCP.

### 7.4 On-disk assertions (paths under `/data`)

| Path | Content |
|---|---|
| `linmail.cfg` | config incl. users + WP records (rewritten by the BBS) |
| `DIRMES.SYS` | binary array of `struct MsgInfo` headers (type/status/to/via/bid/…; first record is a control record) [BPQ-SRC bpqmail.h:615] |
| `Mail/m_%06d.mes` | one file per message body, raw text incl. R: lines — **primary CI assertion target** |
| `WFBID.SYS` | BID database |
| `Mail/Restart/<n>` | partial-transfer restart data |
| `logs/log_YYMMDD_BBS.txt`, `..._TCP.txt` | BBS/TCP logs — grep for `Message Rejected by BID Check`, checksum errors, etc. |
| `Deleted/` | recycle bin |

The m0lte harness asserts by polling `Mail/m_*.mes` for the body string; `docker compose down -v` wipes state between runs.

---

## 8. MUST / SHOULD / LATER

### MUST — full bidirectional forwarding with stock LinBPQ + the classic user surface

| Item | Justification |
|---|---|
| User commands: `?`/H, A, B, X, V, N/Q/Z/Home, I, L family (L, LR, LM, LB/LP/LT, LL n, ranges, L</L>/L@, status letters), R/RM, S/SP/SB/ST/SR/SC with full S-line grammar, K/KM, OP + paging prompts, NODE | "Classic terse BBS surface" — this is the set BPQ users and client software (Winpack-era terminals, paging scripts) expect; the S-line grammar incl. `< from` and `$bid` is also the MBL receive path. |
| Prompt `de <CALL>>`, title/text prompts, `/ex` + Ctrl-Z terminators, `Message: n Bid: … Size: …` acceptance shape | Automated clients pattern-match these exact shapes. |
| Message model: P/B/T; statuses N Y $ F K H D; kill rights; held-invisible rule | Required for list/read semantics and for forwarding state. |
| BID/MID: `<n>_<CALL>` generation, ≤12 chars, case-insensitive dedup store with lifetime, `$bid` parsing, dup → `-`/`NO - BID` | The protocol's identity + loop-prevention backbone. |
| Hierarchical address parse/normalise (WW-rooted, country/continent equivalences), home BBS field | Needed to populate proposals and route. |
| SID exchange both directions; parse per §3.2; advertise `B1FHM$` minimum; tolerate unknown letters; not contain "BPQ" | Gates everything; the compression guard makes B+1 mandatory. |
| FBB B1 forwarding, both roles: FA proposals, `F>` checksum (emit always, verify when present), FS emit `+ - =` / parse `+ Y - N = L H R E !n An`, honour `!n` when sending, SOH/STX/EOT framing, EOT checksum, LZHUF N=2048 codec with CRC16+len32 prefix, ≤5 proposals/block + block-size cap, FF/FQ turn-taking, CRLF line framing | The core requirement: this exact combination is what a stock LinBPQ partner negotiates and the only mode it still speaks to non-BPQ FBB partners. |
| R: line prepend (BPQ shape), loop checks (dup BID + own-call-in-R: + date sanity) | LinBPQ derives age/WP from our R: lines and will hold malformed ones; we need the same protections inbound. |
| Per-partner forwarding config: partner identity (call+SSID exact), enable/interval/send-immediately, connect script (verbatim lines + `C` commands at minimum), TO/AT/HR routing with implied-AT and best-HR-depth single-copy rule for P | Without routing config we can't *originate* forwarding; the single-copy rule prevents dup floods back into the network. |
| MaxRXSize/MaxTXSize guards; `-` on oversize | Protocol-visible behaviour. |
| Housekeeping minimum: kill-by-age per type/state, BID lifetime, K-purge | Keeps the BID store and disk bounded; partner-visible via re-proposal behaviour. |

### SHOULD — high value, not needed for the stock-partner happy path

| Item | Justification |
|---|---|
| B2F (FC proposals + B2 message format + `2` in SID, per-partner opt-in) | LinBPQ speaks it but official guidance is B1 between BBSes; B2F buys multi-recipient + attachments and future Winlink RMS interop. Same framing/codec as B1 ⇒ incremental cost is the header format + FC handling. |
| B0 ("B" without "1") receive support | Trivial once B1 exists (no CRC prefix, no resume); only needed for ancient partners — but cheap insurance since the SID intersection can land there if a partner lacks `1`. |
| WP: consume `TO WP` "On …" updates; `I`-family lookups backed by it; MAY emit updates | Improves addressing; zero protocol risk; partners don't need us to emit. |
| NTS: ST entry, LT, D/delivered, T routing by TO-wildcards, NTS lifetimes | Part of "everything LinBPQ's mail does", but UK deployment rarely exercises NTS; isolate behind the T type. |
| MBL/RLI text forwarding (both roles) + `TEXTFORWARDING` script directive | The universal fallback for dumb PMS/TNC mailboxes; LinBPQ↔us never needs it once B1F works. |
| Resume *granting* (`!n` emission + partial persistence) | Pure robustness optimisation on lossy RF; conformant to never grant. |
| Message filters / hold pipeline (new-user hold, badwords, size-hold), non-delivery notifications | Sysop-quality features that affect partner-visible holds. |
| `FWD`-style sysop forwarding control + `DOHOUSEKEEPING`/`REROUTEMSGS` equivalents | Operability; mirrors the oracle's levers. |

### LATER — explicitly deferred (named, not silently dropped)

| Item | Why deferral is safe |
|---|---|
| FS `H`/`R`/`E` emission | LinBPQ never emits them and treats them as accept/reject anyway; we accept them on parse (MUST) which is all interop needs. |
| `X` (XFWD batch), `A` (personal acks) SID features | FBB-only; LinBPQ ignores both. |
| FB-verb *binary file* transfers | "not yet implemented" in FBB itself [FBB-PROTO]; nobody sends them. |
| BPQ↔BPQ extensions (`; MSGTYPES`, FC extra fields) | Gated on "BPQ" in SID; we deliberately don't trigger them. |
| Winlink RMS/CMS gateway (`;FW:`/`;PQ:`/`;PR:` auth, `rms:`/`smtp:` TO prefixes, POLLRMS/CMSPASS) | Separate product decision; B2F groundwork (SHOULD) keeps the door open. |
| YAPP/file area (FILES/READ/YAPP), PG servers, beacons ("Mail For"/FBB unproto header broadcasts), WebMail-equivalent UI | User-surface extras, no forwarding impact. (Beacons are cheap and nice-to-have for RF presence — first candidate to promote.) |
| OpenBCM telnet 0xFF escaping, MFJ/PMS quirk modes, AEA `/E^Z>` terminator beyond parse-tolerance | Niche legacy peers; add if a real partner appears. |
| Restart-quirk emission (BPQ's 6-byte STX preamble on resume) | Only relevant if we grant resumes (LATER) to a BPQ peer. |
| Import/Export file pseudo-forwarding, SMTP/POP3 interfaces, AMPRNet | Out of scope for the BBS core. |

---

## 9. Verify-against-the-oracle checklist

Ordered by risk × cheapness. Each is a concrete CI probe against the §7 container.

1. **SID handshake end-to-end** — connect as BBS-flagged user, capture LinBPQ's exact SID; send `[PDN-0.1.0-B1FHM$]`; confirm FA-mode negotiation. Also negative-test the compression guard: send `[PDN-0.1.0-FHM$]` (no B) and confirm the documented disconnect. Also confirm our SID *without* "BPQ" doesn't trigger `; MSGTYPES`.
2. **`F>` checksum byte coverage** — proven by fbb_partner.py (sum incl. CR, excl. LF, negated); re-confirm against this LinBPQ version, and that a *wrong* checksum yields `*** Proposal Checksum Error` + disconnect.
3. **EOT checksum coverage** — confirm SOH-header bytes are excluded (source says payload-only); deliberately corrupt one STX byte and observe the error path.
4. **R: line round-trip** — forward a message with our R: format; assert LinBPQ stores it intact, derives age (not held "Corrupt R: Line"), and a second pass through us gets held as looping only on the *second* own-call occurrence.
5. **LZHUF golden vectors** — compress known plaintexts with our codec; decompress LinBPQ's actual STX payload captures (and vice versa). Watch the N=2048 window and the space-filled buffer; a stock-lzhuf (N=4096) port will interop on short messages and corrupt long ones — test with a >4 KB message specifically.
6. **Prompt shapes** — expert vs normal prompt default (doc/source conflict), list header row presence, `KM` kills read-vs-unread (doc/source conflict), name length 12 vs 17.
7. **Dup-BID matrix** — same BID: live P (expect `-`), forwarded P (expect `+` again), B (expect `-`), case-flipped BID (expect `-`), 13-char BID truncation behaviour.
8. **Message entry edge cases** — `/ex` vs Ctrl-Z vs `/EX`; empty title cancel; `$bid` echo in `Bid:` reply.
9. **FA size field tolerance** — propose with size deliberately ±10% of actual: does LinBPQ care? (Source suggests only MaxRXSize gating.)
10. **Resume** — break a transfer mid-STX, reconnect, observe LinBPQ's `!offset` + its 6-byte restart STX preamble; verify our seek-to-offset+6 sender against it.
11. **5-proposal limit** — send 6 proposals in one block; observe behaviour (likely protocol error or ignore — source allocates 5 slots).
12. **B2F (when built)** — negotiate `B12F…`, exchange FC both ways incl. an attachment; confirm per-message (not batched) compression; trailing `0` field tolerance.
13. **MaxFBBBlock units** — set 300 on the oracle, queue two 200-byte messages, count proposals per block (bytes ⇒ 1+1; KB ⇒ 2 together).
14. **MBL fallback** — strip B/F from our SID ($ only) and run an MBL exchange (`OK`/`NO - BID`/prompt shapes).
15. **`I`/`W` SID letters** — flip them in our SID and confirm no behavioural change (expected: inert).
16. **CRLF vs CR framing** — replay proposal blocks with bare-CR framing at speed to characterise the known misparse; lock our emitter to CRLF.

---

## Appendix A — local artifact index (downloaded primary material)

| Path | Content |
|---|---|
| `/tmp/fbbproto/protocole.txt`, `docfwpro.txt`, `docfwcom.txt`, `sid.txt`, `iw3fqg_fbbfwd.txt` | FBB protocol docs (current + Appendix 9/10 + SID letters + older revision) |
| `/tmp/fbbproto/winlink_b2f.txt`, `winlink_dataflow.pdf`, `f4hof_b2f.txt` | B2F spec, Winlink framing PDF, ABNF |
| `/tmp/linbpq-src/`, `/tmp/bpq32-src/` | g8bpq/linbpq source clones (FBBRoutines.c, lzhuf32.c, …) |
| `/tmp/paclink-unix/lzhuf_1.c`, `/tmp/stock_lzhuf.c`, `/tmp/lztest/` | canonical FBB compressor, stock Yoshizaki for diffing, built binaries |
| `/tmp/jnos/fbbfwd.c`, `lzhuf.c` | JNOS cross-check implementation |
| `/tmp/wl2k-go/` | Go B2F/lzhuf implementation (test-vector source) |
| `/tmp/m0lte-linbpq/fbb_partner.py` | the proven scripted FBB peer from Tom's own integration suite |
| `/tmp/bpqmail/` | BPQ docs HTML captures + the four BBS source files |
