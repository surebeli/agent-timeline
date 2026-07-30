# Agent Timeline — Windows

[中文](README.md) · **English**

A translucent desktop timeline widget built with WinUI 3 (Windows App SDK) and C# / .NET 8.
It shares the parsing spec in `docs/SESSION-FORMATS.md`, the visual spec in
`design/design-tokens.json`, and the UI copy in `design/strings.json` with the macOS side.

> ## ✅ Verified on real hardware (latest round 2026-07-30)
>
> This project was originally written on macOS. **Since M3 it has been repeatedly compiled, run
> and verified on real Windows 11 hardware**, and ships at the same version as the macOS side.
> Current state:
>
> | Layer | State |
> |---|---|
> | Build | ✅ VS msbuild x64 Release; six CI gates are hard blockers (mac `swift test` / Core smoke / WinUI msbuild / design-tokens parity / strings-table parity / demo-dataset bilingual invariants) |
> | `Core/` + `Interop/` | ✅ **400 smoke assertions** green (`windows/CoreSmokeTest/`; `dotnet run` reproduces them on any OS) |
> | UI layer (XAML / WinUI / H.NotifyIcon / Win32 interop) | ✅ every item on the layered verification checklist passes; per-item notes in [DEBUG-PLAYBOOK.md](DEBUG-PLAYBOOK.md) §2 (Chinese) |
> | All five agent channels | ✅ claude / codex / grok / kimi / zcode all exercised against real local corpora |
> | Release | ✅ the v0.5.1 package was installed and verified on real hardware (tray persistence / timeline renders / version in the settings title bar) |
>
> **What is still open** (recorded honestly — do not read these as done): the provider engine has
> never been pointed at a real vendor endpoint; a handful of interaction items are
> "mechanism verified, frame-by-frame feel awaiting attended retest". Item by item in
> [Known unverified items](#known-unverified-items-reconciled-2026-07-29) below and in the entries
> marked ⚠️ in DEBUG-PLAYBOOK.
>
> Real-hardware debugging still starts from **[DEBUG-PLAYBOOK.md](DEBUG-PLAYBOOK.md)** (seed-data
> scripts, the layered verification checklist, and the screenshot capture procedure).

## Changelog

> This is a point-in-time translation. [`README.md`](README.md) is the authoritative log — if the
> two ever disagree, the Chinese one is right. Entries are newest first.

- **2026-07-30 — four-language round (B: recognition tables, A: 69-key wiring, plus a recapture)**
  - **B: four-language recognition tables + three Japanese/Korean defects** (`c72824c`). Negation
    sits in a *different position* in each language: Chinese prefixes it, Japanese suffixes it
    (完了して**いない**), Korean does both. Added suffix-negation markers with an 8-character
    window that **stops at a clause boundary**. Korean prefix negation is judged by **어절**
    (space-delimited token), *never* by character — in real corpora `이미 완료` (11,265),
    `제안 완료` (3,261) and `잘못` (84,805) all contain 안 · 못 · 미, so a character test would kill
    the strongest affirmatives. `ClauseSeparators` gained the ASCII period but only in
    **sentence-final form** (otherwise `v0.6.0` gets cut in half), and the forward window went
    24 → 48 to accommodate Korean SOV word order. Compatibility folding deliberately **does not
    use platform NFKC**: `String.Normalize(FormKC)` was measured to **silently return its input
    unchanged** under `InvariantGlobalization=true` — no exception — and the smoke-test project is
    configured that way while the app is not, so copying the platform call would make the gate and
    production disagree, silently. Reasoning in
    [docs/TEXT-NORMALIZATION.md](../docs/TEXT-NORMALIZATION.md) §3.6 (Chinese).
    Two long-standing matching defects were fixed along the way: Latin keywords had no word
    boundary (`prefix`/`suffix` matched `fix` — nearly ubiquitous in developer text), and
    Chinese–Japanese homographs misfired (`要求` / `判断` are high-frequency generic verbs in
    Chinese; measured on 5,189 real commands they misclassified 31 tasks as requirements).
    Quantified against real corpora: codename status attribution **100% unchanged** (789 hits);
    kind attribution 97.36% unchanged with all 137 changes individually attributed.
    Smoke went 360 → **400**, and four mutation tests each turned exactly the intended assertion
    red.
  - **A: 69-key wiring + a language selector in Settings** (`3778ac4`). No UI copy literals remain
    anywhere in the interface. Stored values and display labels are now fully separated
    (`UI/UiText.cs`): `NodeKind` / `CodenameStatus` still persist the Chinese rawValue and filters
    still push down to SQL — language changes rendering only. The filter's "all" entries switched to
    a `::all-projects::` sentinel (a colon is illegal in a Windows path component, so no real
    project name can collide) and are mapped to display text at the boundary. Language changes
    **apply instantly**, and closing the window without saving rolls back — the rollback hangs off
    `Closed` rather than the Cancel button, because the title-bar X never runs Cancel.
    Two more fixes fell out of this: the Settings window's Save/Cancel buttons **were pushed
    outside the scroll area** (the action bar is now pinned outside the `ScrollViewer`), and an
    empty Windows timeline showed nothing at all.
  - **The three overview screenshots were recaptured at 100%** (`35a4ac8`). The capture script now
    changes display scaling itself (`WindowTool scale set`, the same DisplayConfig path the system
    Settings app uses) and parks the pointer off the panel on its own — no human step left. The demo
    config now **pins `Language='ZhHans'`**: it previously didn't, so the output language depended on
    the capture machine's system UI language (this machine is en-US — one run and you get English
    screenshots that look perfectly fine).
  - ⚠️ **This round lost real data once. Two separate bugs; both fixed. The lesson is worth more
    than the fix:**
    1. **`try`/`finally` does not survive the process being hard-killed.** A capture run was
       terminated externally while the demo database was in place and the restore had not run, so the
       demo database stayed at the real location. The *next* run then treated **the demo database as
       the real baseline**, backed it up, and faithfully restored it afterwards — all three ✅ checks
       passed while the real data was cemented over. **Verification green, data gone** is the worst
       failure class there is. Fix: an **in-progress marker** in the data directory (cleared only
       when all three restore checks match), a stable backup location `.shoot-backup`, and a
       `-Recover` switch that restores from it.
    2. **That very guard then deleted the database a second time.** `finally` runs the restore path
       no matter where in `try` the throw happened — including *before* the backup step. At that
       point the backup directory is empty while the restore logic is "delete the live files, then
       copy them back from the backup": the delete succeeds, the copy has nothing to copy, and the
       subsequent app launch rebuilds an empty database and starts backfilling over the evidence.
       Fix: a `$swapped` flag — the restore path touches nothing unless the swap actually happened.
       Both fixes have mutation tests.

- **2026-07-29 (ii) — single-instance guard (closing a cross-platform gap)**
  - macOS has always had this gate in `App/main.swift` (`exit(0)` on finding another process with
    the same bundle id); Windows had no equivalent. The panel sets `IsShownInSwitchers=false`, has no
    taskbar button, and is completely invisible once hidden to the tray — and on Windows 11 the tray
    icon defaults to the overflow area. So when a user wants to check whether it's running, the most
    natural move is to **double-click the exe again**: a routine mistake, not an edge case.
  - Implementation: a new `Program.cs` replaces the XAML-generated `Main` (the csproj defines
    `DISABLE_XAML_GENERATED_MAIN`), and apart from the first line `Main` is **byte-identical** to the
    generated one. The position matches macOS — bail out before any application object exists, so no
    half-initialised process is left behind.
  - **The gate's granularity is one database**, not one machine and not one login session (the mutex
    name is a hash of `AppPaths.DatabaseFile`). Different users have their own `%LOCALAPPDATA%` and
    therefore don't block each other (a fixed-name `Global\` mutex would have), while one user's RDP
    and console sessions share a store and are still caught (a `Local\` prefix would not have caught
    them). If `Global\` can't be created it falls back to `Local\`; if neither works it lets the
    process through — an extra instance is better than an app that won't start.
  - Verified on real hardware: the second process **exits in 83 ms** (exit code 0, ahead of XAML and
    tray init), only one tray icon remains, a **force-kill still allows a restart** with no mutex
    deadlock, and launching three times leaves one. 354 smoke assertions green.

- **2026-07-29 — lead-in continuation verified on real hardware + README overview recaptured
  (v0.5.1 round, no product code changed)**
  - **A: differential run of lead-in continuation.** The §3.3b implementation landed in sync with
    macOS and passed CI; this round added the verification CI cannot do. Across 15,020 real agent
    replies on this machine (claude 5,052 + codex 9,968), `ParserUtil.ResultExcerpt` was run over
    both the pre- and post-change source trees and compared entry by entry: 3,136 outputs changed
    (20.9%), **0 got shorter**, **every old value is a prefix of the new one**, colon-terminated
    4,496 → 1,369, mean length 85 → 127, empty strings 0 → 0. Both hard constraints hold. The
    residual colon count is an order of magnitude above macOS's (2), so it was bucketed and
    attributed: 1,341 replies genuinely have only one paragraph, 19 have all their prose inside
    fences or tables, 9 hit the length cap, 0 unclassified — the implementation is not
    under-reaching. Tooling committed under `scripts/leadin-diff/`.
  - **B: recaptured the README "Windows in the wild" row.** All three shots unified on the same demo
    data, the same dip geometry and the same backplate, at a common 290px width. Capture scripts
    committed under `scripts/shots/`. Four macOS parameters were changed to match Windows reality
    (flyouts are constrained by `ShouldConstrainToRootBounds` and don't overflow the panel; synthetic
    mouse input is swallowed by the system; the UIA tree degrades; the window rect is 7px larger than
    the client area and `PrintWindow` doesn't preserve rounded corners) — all measured and written up
    in [DEBUG-PLAYBOOK.md](DEBUG-PLAYBOOK.md) §3b.
  - **Also fixed** a deviation in `scripts/demo-seed.py` from `docs/DEMO-DATASET.md`: dates were
    hard-coded to 2026-07-26/27 while the spec explicitly says "D = capture day" and macOS had always
    computed them relatively, so the two platforms' screenshots grouped by different dates. Now
    relative to today.
  - **A3**: the CI-produced `AgentTimeline-windows-x64-v0.5.1.zip` was installed and verified (sha256
    matches the Releases page) — tray persistence (confirmed via UIA in the overflow area), timeline
    renders correctly, settings window caption `Agent Timeline 设置 · v0.5.1`.
  - **354 smoke assertions green**, including this round's six in `ResultExcerptLeadIn()`.

- **2026-07-28 (i) — four-way parser comparison: Windows-side divergence fixes (W-a…W-e)**
  - **W-a codex summarizer self-ingestion (high).** When the summary engine resolves to
    `codex exec`, `CliSummarizer` starts the process with cwd
    `%LOCALAPPDATA%\AgentTimeline\summarizer`; codex writes every summary prompt as a `user_message`
    into `~\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`. That path contains neither "AgentTimeline"
    nor "summarizer", so `SessionWatcher.ShouldIgnore`'s path-level exclusion cannot reach it — and
    every summary prompt the app emitted came back in as a user command (a self-ingestion loop).
    Now decided by `session_meta.payload.cwd` (the same criterion as macOS): once
    `FileContext.Disabled` is set the whole file yields zero events, and both the streaming path and
    the restart-resume `EnsureMeta` first-line read go through **the same** `ApplyMeta`. The same
    check was added on the claude side as a second line of defence.
  - **W-b Claude L1 ignore-prefix table (medium).** Windows had 9 entries **with a closing `>`**;
    macOS had 11 matching **bare tag names**. Consequences: injected blocks carrying attributes
    (`<system-reminder priority="high">`, `<bash-stdout exit="0">`, …) failed the prefix match and
    turned into garbage "user command" entries, and `<user_instructions>` /
    `<environment_context>` weren't in the table at all (a whole batch leaked through the claude
    channel). Now byte-identical to macOS's `ParserSupport.ignoredPrefixes`.
  - **W-c Claude assistant multi-text segments (low).** Windows broke out of the loop at the **first**
    segment, so an empty or `text`-less first segment made the entire outcome line vanish. Now
    concatenates every `type=="text"` segment (skipping those without `text`), matching spec §1
    ("concatenate the `type=="text"` segments") and macOS.
  - **W-d Claude project-name carry-over across lines (low).** Not every claude line carries `cwd`;
    Windows independently fell back per line to an escaped directory slug
    (`-Users-x-work-proj`). Now a per-path `FileContext` carries cwd and project name forward.
  - **W-e timestamp tolerance (medium; a rule shared by both platforms).** Each platform had it half
    wrong: macOS dropped the whole line when it couldn't parse, Windows fell back to
    `DateTimeOffset.UtcNow` (which jumps the entry to the top of the timeline pretending it just
    happened — and since ts participates in `UNIQUE(agent,session_id,ts,command_hash)`, a rescan is
    guaranteed to produce duplicate rows). New rule: stay permissive about the shape (every ISO
    variant `DateTimeOffset.TryParse` accepts) → on failure **reuse the last successfully parsed
    timestamp in this file** (the carry happens at the very start of each line's parse, so any
    successful line updates the baseline) → drop the line only if this file has never had a
    timestamp. Landed for Claude and Codex; zcode is a Windows-only parser (macOS still has a lazy
    stub) so it keeps the old `now` fallback with an in-place note, to be unified when macOS
    implements zcode.
  - CoreSmokeTest 266 → 305 assertions green (39 new, including mutation tests that fail if a fix is
    reverted).

- **2026-07-28 (h) — Phase C' cross-platform levelling (W0–W6; every item on the macOS audit list)**
  - **W0 queued-command recovery (was losing user commands).** A prompt typed mid-turn and consumed
    by the current turn survives only as one `attachment.queued_command` record, and Windows had been
    discarding the entire attachment line class. ⚠ It must reuse the same L1 ignore-prefix set —
    **200 of the 217 `queued_command` entries in the local corpus are injected blocks** like
    `<task-notification>`, so skipping the filter would reintroduce the 793 leaks just plugged. Net
    new genuine queued user commands: 17. Verified by rescan: claude entries 81 → 86.
  - **W1 summary retry and an attempt ceiling.** Idempotent `ALTER` for `nodes.summary_attempts`;
    failures back off 1s and retry within the session (previously one timeout meant restarting the
    app), stopping at 3 (previously permanently failing entries re-ran without limit on every
    startup, burning quota); saving settings resets the counter. The engine never touches the Store —
    the decision is injected via a hook.
  - **W2 queue switched to newest-first.** FIFO `Channel` → a `PriorityQueue` keyed on `-ts`, with the
    Channel demoted to a wake-up signal. When backfilling hundreds of entries, the newest ones at the
    top no longer get their LLM titles last.
  - **W3 `SetResultLine` timestamp guard.** The SQL gained `ts<=$ts`, so out-of-order inserts no
    longer attach an old reply to a newer command (`LatestNodeId` within the same file had long done
    this; the two contradicted each other).
  - **W4 inject agent/project context into the prompt.** The prose skeleton is byte-identical to
    macOS's, so the same command can't get a different title/kind on the two platforms; the input cap
    now goes through `DisplayLimits.PromptInput`.
  - **W5 provider alignment.** temperature 0.2 → 0 (summaries should be reproducible), base URL gets
    `/v1` appended automatically (without it you get a straight 404), timeout 30s → 60s.
  - **W6 `Clip` switched to grapheme clusters.** Guarding surrogate pairs isn't enough — ZWJ families,
    variation selectors and combining marks all get split down the middle. Now the same measure as
    macOS's `String.count`.
  - **Found while verifying on real hardware**: `!cmd` shell-passthrough records leaked into the
    timeline as two entries (20 in the corpus) — `<bash-stdout>`/`<bash-stderr>` joined the L1 ignore
    prefixes, and `<bash-input>` is converted to `$ cmd` and kept.
  - CoreSmokeTest 225 → 253 assertions green; the 12 items in `docs/TEXT-NORMALIZATION.md` §4.1 are
    now level, and §4.2 holds only the macOS zcode parser plus two undecided cross-platform items.

- **2026-07-27 (g) — attended real-hardware feedback fixes + the zcode channel lit up**
  - **P1 (from attended testing).** In-panel flyouts (chip detail / dictionary / context menu / filter
    menu) are separately windowed popups: opening one either steals activation or fires
    `PointerExited`, dropping the main window to idle 0.25 — leaving a readable popup floating over an
    almost transparent panel. All six flyouts now register Opened/Closed uniformly, pin the window at
    hover opacity while any is open, and only fall back once all are closed and the pointer is
    outside the panel (measured: 242 pins / 64 releases).
  - **P2.** `OnNodeAdded` rebuilt the whole list per entry, O(N²) → a scheduling queue coalesces into
    one pump, one rebuild. `EnsureLoaded`'s 50-page loop converged from rebuilding per page to once
    on hit.
  - **P3.** Summary JSON switched to balanced-candidate enumeration, back-to-front (immune to stray
    braces in codex stdout); title / key-point / definition truncation is surrogate-pair safe (emoji
    no longer truncate into U+FFFD); `AppSettings.Save` got a lock plus atomic replace; panel size
    scales by `GetDpiForWindow` (converging known item #6); an LLM reclassification under an active
    kind filter now adds or removes the entry's membership immediately.
  - **zcode channel.** The user confirmed sessions live in `~\.zcode\cli\agents` (one directory per
    task under `sess_*\agent_*\`). `ZcodeParser` was implemented from real samples: `transcript.jsonl`'s
    `turn_started.payload.input` → task command entry, `turn_complete.payload.response` → outcome line
    plus codename mining; the sidecar `metadata.json`'s cwd → project name. The default root is
    watched automatically when `EnableZcode` is on (the default); settings can override it. Verified
    by backfilling 36 task entries (hawk-watcher). CoreSmokeTest 90 → 110 assertions.
    ⚠️ `docs/SESSION-FORMATS.md` §4 (shared by both platforms) still needs the spec filled in per the
    report and the macOS parser brought in line.
  - Erratum: the machine recorded in entry (f) runs at 1706x960 @100% scaling (remote display), not
    150% — the DPI fix is an identity transform on this machine and takes effect on high-DPI ones.

- **2026-07-26 (f) — M3 real-hardware verification complete (Win11 Enterprise 26200, first
  end-to-end run on real hardware)**
  - **11 fixes found on real hardware** (see that day's fix commits): seed script UTF-8 BOM (PS 5.1
    misreads it as GBK); pagination cursor id-only → composite (ts,id) (multi-agent backfill was
    guaranteed to drop rows; CoreSmokeTest 85 → 90 assertions); watcher built-in root pre-creation +
    an Error rescan + offset-persistence ordering; CLI summary prompt moved to stdin (escaping and
    injection problems going through cmd.exe for a `.cmd` shim meant the CLI engine had *always*
    silently degraded on Windows) + killing the whole process tree on timeout + finishing as soon as
    the result envelope arrives (a user-side SessionEnd hook was keeping the process alive) + PATH
    quoting tolerance; sticky day-header layout recalibration (froze during jump scrolling); losing
    focus no longer rewrites `IsInputActive` (Acrylic collapsed to solid when unfocused); tray
    `ForceCreate` with EcoQoS efficiency mode off; tray exit zombie guard (#5931, `Close` +
    `Environment.Exit` as a backstop); remembered window coordinates fall back when off-screen;
    header filters switched to compact Button + MenuFlyout (340px can't fit two ComboBoxes) with the
    title column allowed to elide.
  - **Final platform deviations:**
    1. the sticky day header is simulated via ViewChanged plus post-layout recalibration (macOS has a
       native sticky section header), so jump scrolling has a one-frame calibration lag —
       imperceptible in practice;
    2. window hover/idle fading actually uses `opacity.transitionMs` (180ms); the tokens'
       `hoverFadeMs` (120ms) is only for the in-entry hover fade — same source, same meaning as macOS;
    3. header filters are compact "全部 ▾ / 类型 ▾" buttons plus single-select menus (macOS uses popup
       buttons); the title elides when space runs short and long project names truncate inside the
       button (WinUI control chrome is wider than macOS's);
    4. "always on top" was refused at system level on the verification machine (that session forbids
       topmost for *every* window, Notepad included); the code path is correct and needs a retest in a
       normal interactive session — not an app defect;
    5. WinUI `Border` strokes inset 9/7px vs macOS's 8/6px (a pre-existing known item, deliberately
       not compensated);
    6. the 30s CLI summary timeout is tight for a heavyweight claude setup ("haiku routed to a large
       model + hooks attached"), mitigated by finishing early on the result envelope; a plain haiku
       setup doesn't hit it.
  - Known-unverified items converged: the NuGet versions work as pinned; Acrylic and layered alpha
    coexist fine on this machine's 26200 build (the `UseLayeredWindowAlpha` escape hatch isn't
    needed); borderless drag and edge-resize hit regions measured correct; `ItemsRepeater`
    DataContext / TemplateSelector measured fine; tray icon and menu measured fine. Still uncovered:
    the Kimi channel (no local data), a real provider endpoint, real-mouse selection / hover receipt /
    context menu (session environment limits), and the single-instance guard (knowingly skipped).

- **2026-07-26 (e) — self-stabilizing contrast over any background (matching the last clause of
  macOS PRD §3.2b)**
  - New `color.panelScrim` (light `#F5F6F7B8` / dark `#14161C8C`): `RootGrid`'s background becomes a
    scrim layer between the DesktopAcrylic material and all content — compressing the variance of the
    colour bleeding through (dark IDEs and terminals being the norm) while still letting light in.
    Window opacity behaviour is unchanged.
  - New `color.surfaceStroke` (light `#0000001A` / dark `#FFFFFF24`): both paper levels (command block
    and derived block) get a 1px adaptive stroke (`Border` BorderBrush/Thickness, corner radii
    unchanged at 3,8,8,8 / 8), so blocks have their own boundary on same-hue backgrounds. The
    agent-coloured ink line still stacks above the stroke (fill → stroke → rule, same order as macOS).
  - Dark values retuned: commandBg → `#2E3542D9`, derivedBg → `#242A36B4`, timelineRail → `#454B59`,
    entryDivider → `#FFFFFF1C`, derivedRule → `#565D6BA6`.
  - The Assets JSON is byte-identical to the root `design/` copy; `Themes/Tokens.xaml` (AARRGGBB
    conversion, Dark kept in sync with Default) and the dual-colour load table in `DesignTokens.cs`
    were updated.

- **2026-07-26 (d) — derived-block contrast correction (matching macOS feedback the same day)**
  - The derived block sits on its own secondary paper: new `color.derivedBg` (light `#FFFFFF8C` /
    dark `#242A36A8`), `Border` corner radius 8 (plain corners, no flattened top-left), padding 8×6;
    the 14px indent stays outside the paper while the dashed ink line moves inside it.
  - `derivedRule` (light `#A9AFBB` / dark `#4A505E99`) and `dayHeaderRule` (light `#00000022` / dark
    `#FFFFFF26`) brightened; the time in the meta row and the collapsed key-point summary moved from
    textTertiary up to textSecondary.
  - The Assets JSON was made byte-identical to the root `design/` copy again, with `Tokens.xaml`
    (AARRGGBB conversion) and the dual-colour load table in `DesignTokens.cs` kept in sync.

- **2026-07-26 (c) — "dual-ink ledger" timeline visual rework (matching macOS PRD §3.2b)**
  - Entries became frameless ledger rows: a 1px `entryDivider` hairline (inset past the 22px rail
    gutter), with requirement/decision rows getting an 8% kind-colour wash across the whole row
    (radius 6). The old card border/background and the "expand to see the original" block were
    removed.
  - **The command block is the protagonist**: your words are always visible (3 lines collapsed / full
    text expanded) on a high-opacity `commandBg` paper block (CornerRadius 3,8,8,8 — top-left
    flattened to point at the rail), with a 2px solid agent-coloured ink line on the left edge, a
    Cascadia Code "❯" hanging-indent column at 14px, and selectable body text in Segoe UI Variable
    13.5 SemiBold `commandText`.
  - **The derived block**: 14px indent plus a 1px dashed vertical ink line (`Line StrokeDashArray
    2,3`), a ✦ and a demoted title (hidden when the command is ≤20 characters or the title merely
    repeats a normalized prefix of it), a single-line key-point summary joined by " · " with an accent
    "+n" counter (expanding to the full list), chips (hit area expanded 4px), and a green outcome
    line.
  - **Rail grammar**: a continuous 2px track segment per entry; requirement/decision = a kind-coloured
    diamond (~9px rotated rectangle), task/fix/research/learning = a 7px filled circle, other/
    unclassified = a 5px hollow circle. Entries that define a codename get an accent ring (1.5px
    stroke, 2.5px outset).
  - **Day grouping**: grouped by calendar day (今天 · n 条 / 昨天 / MM-dd · weekday), with an inline
    divider row plus a pinned sticky day bar driven by ViewChanged (dayHeaderBg backing,
    CharacterSpacing 120, a 6px track tick).
  - **Interaction**: click anywhere on the row to expand (only background and meta row are hit-testable;
    text selection wins), the chevron rotates 180° on expand, hover surfaces an `entryHover`
    background plus a copy button for the original text (✓ green receipt for 800ms), and a context
    menu (copy original / copy summary / jump to definition / filter to this project). Motion is
    opacity only (120ms hover fade-in) and respects the system's `UISettings.AnimationsEnabled`.
  - Tokens synced in three places: the Assets JSON byte-identical to root `design/` (command*/
    derivedRule/entryHover/entryDivider/dayHeader* colours; command/derivedTitle/dayHeader sizes and
    tracking; rail/ink-line/indent spacing; commandBlock/anchorWash radii; marker/lineLimit/glyph/
    motion blocks), with `Tokens.xaml` and `DesignTokens.cs` gaining the corresponding resources and
    parsing.

- **2026-07-26 (b) — adversarial revision of detection semantics (matching five macOS changes the
  same day)**
  - The definition regex was replaced wholesale: the lead-in accepts colons / ASCII commas /
    whitespace, the codename may be `**bold**`, and the definition body excludes the ideographic
    comma and ASCII comma and cuts off at the next inline "CODE:" via negative lookahead — so inline
    forms like "编号如下：N1: 登录, N2: 支付", "- **N1**: xxx" and replay-flattened space-separated
    lists all parse.
  - The stop list is stored normalized (compare uppercase with hyphens and dots stripped, via
    `IsStopped`) and expanded with technical/planning short codes (S3/EC2/R2/B2/K8/X86/X64/I18N/
    L10N/V1–V5/Q1–Q4/H1/H2/P0–P2/MP3/MP4). A new `IsPlausibleName` gate (2–24 characters, contains a
    letter, not stopped) filters LLM-extracted codenames on both the registry and summary-JSON sides.
  - Status-keyword negation: a hit is ignored if 未没不别无非 appears within the two characters before
    the keyword ("尚未完成" / "不执行" no longer set a status).
  - `ProcessText` self-mention exclusion: codenames defined in this pass don't take part in the
    subsequent mention scan (a definition sentence is not a status update about itself, and define
    already counted it); codenames newly registered via the dash channel in this pass are touched
    with `bumpOccurrence=false` so they aren't double-counted.
  - The replay marker became a persisted integer `AppSettings.CodenameReplayVersion` (currently 3,
    stored in settings.json), replacing a column-existence check. The marker is written only **after**
    replay completes (a crash mid-way re-runs automatically), and the watcher and summary engine now
    start from the replay-complete callback.
  - CoreSmokeTest gained scenarios for the four definition forms / the stop list / negation contexts /
    definitions not being self-mentions — 85 assertions, all passing.

- **2026-07-26 — codename lifecycle + kind anchors (matching macOS PRD §3.3 / §3.3b)**
  - `Core/CodenameDetector.cs` (new): three detection channels fully shared with macOS — the
    hyphenated long-codename regex, the `N1: xxx` definition form (including the full-width colon and
    clause boundaries), and exact matching of dictionary-known short codes (ASCII word boundaries plus
    clause-window status inference for done / changed / in progress).
  - `Store`: a `codenames` table migration (status / status_node / updated / last_context columns)
    plus `nodes.kind` / `summaries.kind` columns; `DefineCodename` (latest definition wins; a rewritten
    definition automatically sets "changed") / `RecordCodename` / `TouchCodename`; a one-shot
    `NeedsCodenameReplay` marker for history.
  - `TimelineCoordinator`: mining the full text of agent replies (`TaskComplete.FullText` → attributed
    to the latest entry) plus a one-shot `ReplayCodenamesIfNeeded` at startup; a `CodenamesChanged`
    event drives chip badge refreshes.
  - Summary JSON contract upgraded: `kind` (requirement|task|research|learning|decision|fix|other)
    plus a codename `status` (defined|in progress|done|changed|mentioned); `RuleSummarizer` falls back
    to keyword-based `GuessKind`.
  - UI: coloured kind tags on entries (tokens `color.kind`), a kind filter dropdown, chip status
    badges ✓/△/▶, status / latest mention / update time in the chip flyout, and a codename dictionary
    panel in the header (sorted by most recently updated; click to jump to the defining entry).
  - Tokens: `Assets/design-tokens.json` re-synced with the root `design/design-tokens.json` (adding
    `color.statusChanged` and `color.kind`), with `Themes/Tokens.xaml` gaining the corresponding
    resources.

## Requirements

- Windows 10 1809 (build 17763) or newer; Windows 11 recommended (Acrylic looks best there).
- **Visual Studio 2022** (17.8+) with these workloads / components:
  - **.NET desktop development**;
  - **Windows application development** (includes the Windows App SDK C# templates and the
    Windows 10/11 SDK);
  - .NET 8 SDK (bundled with VS 17.8+).

## Open and run

1. Open `windows/AgentTimeline.sln` in VS 2022.
2. On first open, wait for the NuGet restore (Microsoft.WindowsAppSDK / H.NotifyIcon.WinUI /
   Microsoft.Data.Sqlite).
3. Select the **Debug | x64** configuration.
4. Hit F5. The project is **unpackaged** (`WindowsPackageType=None`) with
   `WindowsAppSDKSelfContained=true`, so there is no MSIX to deploy and no Windows App SDK runtime to
   pre-install.

Once running:

- the floating panel appears in the top-right of the primary display (on first run); drag the header
  to move it and pull the edges to change its width (280–560);
- a tray icon appears: left-click toggles show/hide, and the right-click menu has show/hide, always
  on top, settings, and exit;
- clicking close or pressing Alt+F4 only hides to the tray — a real exit goes through the tray menu's
  "exit".

## Where data and settings live

| What | Path |
|---|---|
| Settings | `%LOCALAPPDATA%\AgentTimeline\settings.json` |
| SQLite (entries / codename dictionary / file offsets / summary cache) | `%LOCALAPPDATA%\AgentTimeline\timeline.db` |
| Log | `%LOCALAPPDATA%\AgentTimeline\logs\app.log` |
| CLI summarizer working directory | `%LOCALAPPDATA%\AgentTimeline\summarizer` |

Watched session directories (see `docs/SESSION-FORMATS.md`; `~` → `%USERPROFILE%`):

- Claude Code: `%USERPROFILE%\.claude\projects\**\*.jsonl`
- Codex: `%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`
- Kimi Code: `%USERPROFILE%\.kimi-code\sessions\wd_<project>_<12hex>\session_<uuid>\agents\main\wire.jsonl`
  (changed 2026-07-28: the old `.kimi\sessions` layout and the TurnBegin/ContentPart protocol are no
  longer supported)
- zcode: `%USERPROFILE%\.zcode\cli\agents\sess_<uuid>\agent_<uuid>\transcript.jsonl`
  (the default root is watched automatically; to change it, edit `ZcodeSessionRoot` in settings.json)

## Design tokens

**`design/design-tokens.json` at the repo root is the single source of truth.**
Within this project, `AgentTimeline/Assets/design-tokens.json` is a copy of it (read at runtime by
`DesignTokens.cs` for opacity / sizes / agent colours), and `AgentTimeline/Themes/Tokens.xaml` is a
XAML resource set generated by hand from the same JSON (colours / font sizes / spacing / radii).
When you change tokens, update all three: root JSON → copy into Assets → regenerate Tokens.xaml.
Note that XAML colours are `#AARRGGBB` while tokens are `#RRGGBBAA` — the alpha is in a different
position.

UI copy works the same way: `design/strings.json` (69 keys × 4 languages) is the single source of
truth, `AgentTimeline/Assets/strings.json` is its byte-identical copy, and CI fails if they drift.

## Module layout

```
AgentTimeline/
├── App.xaml(.cs)               # composition root: settings/store/registry/engine/coordinator
├── MainWindow.xaml(.cs)        # floating panel: borderless + Acrylic + hover opacity + tray + timeline UI
├── SettingsWindow.xaml(.cs)    # settings window (F6)
├── AppStrings.cs               # loads Assets/strings.json; language resolution and lookup
├── DesignTokens.cs             # parses Assets/design-tokens.json
├── Themes/Tokens.xaml          # XAML resources generated from tokens
├── UI/
│   ├── TimelineViewModel.cs    # timeline VM (reverse order, paging, filters, entry VMs)
│   ├── UiText.cs               # the one mapping from stored values to display labels
│   └── OpacityAnimator.cs      # hover 0.95 / unfocused 0.25, fast in / slow out
├── Interop/
│   ├── WindowInterop.cs        # layered-window alpha + borderless drag (Win32)
│   └── FileIdentity.cs         # file id (the inode equivalent; detects file recreation)
└── Core/                       # mirrors the macOS Core (namespace AgentTimeline.Core)
    ├── Models.cs               # AgentKind/UserCommand/TaskComplete/Summary/TimelineNode/CodenameEntry
    │                           #   + CodenameStatus/NodeKind (lifecycle and kind labels)
    ├── Store.cs                # SQLite: nodes/summaries/codenames/file_offsets (WAL) + lifecycle migrations
    ├── CodenameDetector.cs     # codename detection: long-code regex / definition form / dictionary-led short codes (shared with macOS)
    ├── CodenameRegistry.cs     # codename dictionary: union of command + reply + LLM, state machine persistence and cache
    ├── SessionWatcher.cs       # FileSystemWatcher + byte-offset incremental tail + 7-day backfill
    ├── TimelineCoordinator.cs  # dataflow orchestration (watcher→parser→store→engine→UI events)
    ├── Text/TextNormalizer.cs  # display normalization + the match-mode compatibility fold
    ├── Parsers/                # Claude/Codex/Grok/Kimi/ZCode per spec
    └── Summarize/              # SummaryEngine + Cli/Provider/Rule implementations
```

## About the summary engine

- Default "local CLI": invokes `claude -p <prompt> --output-format json --model haiku` (falling back
  to `codex exec` when claude isn't on PATH); 30-second timeout, automatic degradation to a rule
  summary on failure with a retry marker.
- The CLI working directory is fixed to `%LOCALAPPDATA%\AgentTimeline\summarizer`, and
  `SessionWatcher` **ignores** Claude sessions produced there (preventing a self-summarization loop).
- "Custom provider": OpenAI-compatible `/chat/completions`; fill in Base URL / Key / Model in
  Settings.
- "Rules only": no LLM at all — the first line becomes the title and codenames come from regexes.

## Known unverified items (reconciled 2026-07-29)

The original seven "check these first when debugging on Windows" items, reconciled — **five closed,
two still open**.

| # | Item | State |
|---|---|---|
| 1 | **NuGet versions** (`WindowsAppSDK 1.5.240627000` / `H.NotifyIcon.WinUI 2.0.131` / `Data.Sqlite 8.0.6`) | ✅ **work as pinned**, never upgraded |
| 2 | **Layered-window alpha vs Acrylic compatibility** | ✅ they **coexist fine** on this machine's Win11 26200; `OpacityAnimator.UseLayeredWindowAlpha` stays `true` and the escape hatch is unused |
| 3 | **Borderless drag** (`WM_NCLBUTTONDOWN + HTCAPTION`) | ✅ measured working; the `WM_NCHITTEST` hit regions for edge resize and the width clamping were measured correct too |
| 4 | **DataContext in an `ItemsRepeater` DataTemplate** | ✅ measured fine; `TimelineItemTemplateSelector` stays correct across 4,900+ entries |
| 5 | **Kimi Code wire protocol** | ✅ **exercised against real local corpora** (2026-07-29: 120 `wire.jsonl` files / 177 replies parsed correctly). DEBUG-PLAYBOOK §2b's earlier note that "there's no kimi data locally, this channel is uncovered" no longer holds |
| 6 | **Window size DPI** | ✅ fixed: `RestoreWindowBounds` multiplies token sizes by `GetWindowScale`; sizes the user saved are already physical pixels and are restored as-is |
| 7 | **Single-instance guard** | ✅ **implemented** (2026-07-29) — `Program.cs` replaces the XAML-generated `Main` and passes a named-mutex gate at the entry point, same position and same semantics as macOS's `App/main.swift`. The gate's granularity is **one database** (the name hashes `AppPaths.DatabaseFile`), so different users don't block each other while one user across sessions is still caught |

Two more items that weren't among the original seven, for completeness:

- **The provider engine**: ✅ **the whole chain works** (2026-07-29, `scripts/provider-check/`) — a
  baseUrl without `/v1` is completed automatically, the Bearer header is sent, `temperature=0`,
  `choices[0].message.content` is parsed → `SummaryJson.Parse` → persisted with
  `summary_source='Provider'` and the title replaced by the LLM's value; all five checks green. The
  endpoint was a local OpenAI-compatible mock (real HTTP, real protocol).
  ⚠ **Still unverified**: the response quirks of any *specific* vendor endpoint. That needs real
  vendor credentials, which should not pass through a script — fill in Base URL / Key / Model in
  Settings yourself, then check `logs\app.log` and `summary_source` in the database.
- **Interactions that need a real pointer**: an attended retest on 2026-07-29 found and fixed three
  genuine defects —
  - ✅ **click anywhere to expand**: `Tapped` used to hang off a transparent hit layer sandwiched in
    the middle, while the command and derived paper blocks are **opaque Borders, hit-testable, and
    have no handler of their own** — so clicking them bubbled the event *up* only, and the hit layer
    (a sibling, below them) never got a turn. The clickable area was reduced to the meta row and the
    thin gaps between blocks. Fixed by moving `Tapped` to the entry root (matching macOS's
    `.contentShape(Rectangle()).onTapGesture`) and deleting the hit layer;
  - ✅ **selecting the key-point summary line**: in the collapsed state it's the most prominent line
    in the derived area, and it was the only text in the whole entry missing
    `IsTextSelectionEnabled`;
  - ✅ **context menu in the derived area**: a `TextBlock` with `IsTextSelectionEnabled="True"`
    **swallows the right-click gesture**, so it never reaches `Entry_RightTapped` on the entry root.
    The command area has large non-text regions (the ❯ column, Border padding, right-hand slack), so
    a right-click there still bubbled up — which is why the symptom looked like "only the command
    area has a menu". Fix: add **element-level** `RightTapped` to each text element in the entry;
    adding it worked, which confirms the diagnosis.
  - ⏳ **still not retested**: the hover copy ✓ receipt, and frame-by-frame smoothness while flicking
    the scroll wheel fast.

Also: the hyphenated-codename regex is `\b[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3}\b` (shared with
macOS's `CodenameDetector`). The first segment's quantifier is `{0,9}` and not `{1,9}` — otherwise the
PRD's own example `T-PLUGIN-00` (a single-letter first segment, T) can't match and you'd only get
`PLUGIN-00` (verified by the smoke test). Short codes (`N1` / `T2`) enter the dictionary only via the
`N1: xxx` definition form or dictionary-led matching, never by bare matching.

## Mapping to the PRD

- F1 session tracking: `SessionWatcher` + five parsers ✅
- F2/F2b timeline display: reverse order, dual-ink ledger entries (command block as protagonist + ✦
  derived block + rail markers + day grouping), project and kind filters, original command always
  visible and selectable/copyable ✅
- F3 codename dictionary (with lifecycle): union of definition-form registration, dictionary-led
  matching and LLM extraction; state machine (defined → in progress → done/changed); restated
  definitions take effect; chip status badges and flyout; a dictionary overview panel; one-shot
  replay of history ✅
- F4 summary engine: CLI / Provider / Rule implementations + hash cache + serialized rate limiting +
  degradation ✅
- F5 window interaction: tray, two opacity levels with animation, always-on-top toggle, position and
  size memory ✅
  ("an inactive panel that doesn't steal focus" is an NSPanel trait on macOS with no direct Windows
  equivalent; not implemented)
- F6 settings: engine / UI language / opacity / always on top / backfill days / per-agent toggles ✅
  (the version is in the title bar, the same string on both platforms)
- Four-language UI: `design/strings.json` (69 keys × 4 languages) + the `AppStrings.cs` loader +
  `UI/UiText.cs` mapping stored values to display labels; recognition tables are always on in all
  four languages (the session's language is unrelated to the UI's) ✅
