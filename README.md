# SpawnSpotter

Logs the process chain behind every focus change you didn't ask for.

A Windows 11 tool that catches the sub-second window flashes (cmd.exe, conhost.exe, docker.exe, node.exe, etc.) that steal keyboard focus and corrupt typing. For each "steal" event it records UTC timestamp, the focused window, the full parent process chain (PID, image path, command line, cwd), and how long it had been since you actually typed or clicked.

Runs as a standard user — no admin, no kernel driver, no service install. Single 11 MB self-contained .exe (Native AOT).

---

## Quick start

```powershell
# Build once
dotnet publish ./SpawnSpotter.csproj -c Release -r win-x64

# Run with the verbose log (default writes CSV + JSONL to %LOCALAPPDATA%\SpawnSpotter\logs)
.\bin\Release\net10.0\win-x64\publish\SpawnSpotter.exe watch -v 1
```

Press Ctrl+C to stop. Exit summary prints a count of each classification and the log directory.

For a bounded run:

```powershell
.\SpawnSpotter.exe watch --duration 30m --max-steals 10
.\SpawnSpotter.exe watch --duration 24h --format csv,jsonl,html --mode silent
```

---

## CLI

`spawnspotter` with no arguments prints the top-level help. Two subcommands:

| Command | Purpose |
|---|---|
| `spawnspotter watch [options]` | Start logging. The only "doing" command. |
| `spawnspotter version` | Print version + git commit and exit. |

### `watch` flags

| Flag | Default | Description |
|---|---|---|
| `--log-dir <PATH>` | `%LOCALAPPDATA%\SpawnSpotter\logs` | Output directory (created if missing). |
| `-f, --format <LIST>` | `csv,jsonl` | Comma-separated subset of `csv,jsonl,logfmt,md,log,html`. |
| `-m, --mode <MODE>` | `interactive` | One of `interactive` (live status line + scrolling events), `silent` (no UI; file logs only), `status-only` (status line, no per-event lines). |
| `-d, --duration <SPAN>` | (unset = forever) | Auto-stop after this span. Examples: `90s`, `45m`, `2h`, `1d`, `2h30m`. |
| `--max-steals <N>` | (unset) | Stop after N STEAL events. Combines with `--duration` (whichever first). |
| `-v, --verbosity <0..3>` | `0` | `0`=STEAL+SESSION_LOCK only · `1`=+USER_* · `2`=+diagnostics · `3`=+raw event stream (key **categories** only — never key contents). |
| `--threshold-ms <INT>` | `500` | Classifier window for "input preceded this focus change?" (ms). |
| `--threshold-alt-tab-ms <INT>` | = `--threshold-ms` | Per-source override for Alt+Tab. |
| `--threshold-click-ms <INT>` | = `--threshold-ms` | Per-source override for click. |
| `--threshold-other-ms <INT>` | = `--threshold-ms` | Per-source override for other system keys. |
| `--dedupe-window-ms <INT>` | `50` | Drops same-HWND duplicates across the three WinEvent sources within this window. |
| `--max-chain-depth <INT>` | `20` | Safety cap on parent-chain walker. |
| `--ignore-class <PATTERN>` | (none) | Glob matched against the new window's class name. Drops matching events. Repeatable. |
| `--ignore-image <PATTERN>` | (none) | Glob matched against the focused image basename. Drops matching events. Repeatable. |
| `--locked-hwnd-ttl-min <INT>` | `5` | Minutes of no user input after which the "what was the user really doing?" anchor is cleared. `0` disables the timeout. |
| `--capture-env` | off | Capture full per-process env (`KEY=VALUE`) into JSONL chain nodes. **WARNING: secrets land in logs.** |
| `--enricher-workers <N>` | `max(2, ProcessorCount/4)` | Parallel enrichment workers in the pipeline (stage 2). |

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Graceful shutdown (Ctrl+C or `--duration` / `--max-steals` expired) |
| `1` | Startup error (hook install failed) |
| `2` | Bad CLI arguments |
| other non-zero | Unhandled exception |

---

## Architecture

Three design properties drive everything:

1. **Hook callbacks must finish in microseconds.** Windows applies `LowLevelHooksTimeout` (default 300 ms) to `WH_KEYBOARD_LL` / `WH_MOUSE_LL`; an unresponsive hook makes the entire mouse feel sluggish. So all real work runs after the callback returns, off the hook thread.
2. **Hooks must be owned by a thread with a message pump.** `SetWindowsHookEx` and `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT` dispatch their callbacks via the installing thread's `GetMessage` loop. No pump = no callbacks fire.
3. **The keyboard hook is a privacy boundary.** Raw `vkCode` is consumed inside the callback and discarded; nothing about a specific keystroke survives past the post.

### Single producer thread

All five hooks share one STA thread with one message pump. Callbacks are sub-microsecond by construction (categorize, stamp seq + tick + UTC, post to a Dataflow buffer) — so there's no benefit to per-hook threads, and one thread gives us strict seq-order at ingress (no inter-thread race where a preempted thread posts seq=42 after another thread posts seq=43).

```
+---------------------------------------+
| SpawnSpotter.Hooks (1 STA thread)     |
| - GetMessage loop                     |
| - Hidden HWND for WM_DISPLAYCHANGE /  |
|   WM_DPICHANGED (monitor topology)    |
|                                       |
| Hosts:                                |
|   - WH_MOUSE_LL                       |
|   - WH_KEYBOARD_LL                    |
|   - SetWinEventHook EVENT_SYSTEM_FOREGROUND |
|   - SetWinEventHook EVENT_OBJECT_SHOW |
|   - SetWinEventHook EVENT_OBJECT_FOCUS|
|                                       |
| Per callback: build readonly struct + |
| Post via EventBus. No filtering /     |
| enrichment / state-machine work here. |
| (WH_MOUSE_LL drops WM_MOUSEMOVE       |
| at the switch; WinEvent SHOW/FOCUS    |
| drop non-top-level windows via cheap  |
| in-process Win32 calls.)              |
+---------------------------------------+
```

Each callback also captures the OS-recorded event time (`KBDLLHOOKSTRUCT.time`, `MSLLHOOKSTRUCT.time`, `dwmsEventTime`) and reconstructs the full 64-bit timestamp via unsigned rollback math, so the record's timestamp reflects when the OS observed the event rather than when our callback ran.

### Unified pipeline (TPL Dataflow)

Every event — keyboard, mouse, all three WinEvent kinds — flows through one pipeline. The classifier branches on the event kind: input events update its sink-local last-X timestamps; window events get dedup'd, classified, emitted.

```
        +------------------------------------------------+
        | BufferBlock<RawHookEvent>                      |
        | BoundedCapacity = 1024                         |
        | All 5 hooks post here via EventBus             |
        +------------------------------------------------+
                                |
                                v
        +------------------------------------------------+
        | TransformManyBlock<RawHookEvent, EnrichedEvent>|
        |   MaxDegreeOfParallelism = N                   |
        |     default = max(2, ProcessorCount/4)         |
        |   EnsureOrdered = true                         |
        |                                                |
        | Per event, branches on Kind:                   |
        |   Window event ->                              |
        |     GetWindowThreadProcessId,                  |
        |     GetClassNameW / GetWindowTextW,            |
        |     ProcessReader.TrySnapshot (focused PID),   |
        |     parent + ancestor chain walk               |
        |     via NtQueryInformationProcess +            |
        |     ReadProcessMemory PEB walk                 |
        |   Input event -> passthrough                   |
        |                                                |
        | Pressure detection: on dequeue, if the buffer  |
        | crossed 90% full (or back below 70%), prepend  |
        | a PipelinePressureEnter/Clear event to the     |
        | output for this input.                         |
        +------------------------------------------------+
                                |
                    in original Seq# order
                                |
                                v
        +------------------------------------------------+
        | ActionBlock<EnrichedEvent>                     |
        |   MaxDegreeOfParallelism = 1                   |
        |   EnsureOrdered = true                         |
        |                                                |
        | Per event, branches on Kind:                   |
        |   Input event ->                               |
        |     update last-X tick (key / mouse-down /     |
        |     alt-tab / system-key)                      |
        |   Window event ->                              |
        |     dedupe (hwnd + tickMs window),             |
        |     classify (SESSION_LOCK / monitor-topology /|
        |       ignore-glob / threshold-based),          |
        |     update locked-anchor bookkeeping,          |
        |     post EventRecord to broadcast              |
        |   Pressure event ->                            |
        |     emit PIPELINE_PRESSURE record to broadcast |
        +------------------------------------------------+
                                |
                                v
        +------------------------------------------------+
        | BroadcastBlock<EventRecord>                    |
        |   identity clone (records are immutable)       |
        +------------------------------------------------+
                                |
                                v
                One ActionBlock per consumer (DOP=1):
                  - Console UX (per-event lines + status)
                  - CSV exporter
                  - JSONL exporter
                  - logfmt exporter
                  - Markdown exporter
                  - Plain-text exporter
                  - HTML in-memory accumulator
                  - Shutdown-watcher (--max-steals)

                Each consumer has its own queue and back-pressure
                boundary; a slow exporter cannot delay the others.
                Per-consumer verbosity filtering via LinkTo predicate.
```

`EnsureOrdered = true` on the parallel enricher means output order equals input order even when one worker finishes a 10 ms enrichment while another finishes a sub-µs passthrough — the fast one waits for the slow one. The classifier sees events in true seq-order (= post-order = real-time order, since posts are serialized through one producer thread).

No reorder buffer, no timing window, no merge race. The ordering correctness is structural.

### Native AOT

`PublishAot=true`. All P/Invoke uses `[LibraryImport]` source-generated marshaling; hook callbacks are `static partial` methods marked `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]` with addresses passed via the `&Callback` operator — no managed delegates, no `Marshal.GetFunctionPointerForDelegate`, no `GCHandle.Alloc` pinning. JSON output uses a source-generated `JsonSerializerContext`. The shipped binary is a single ~11 MB .exe with no .NET runtime dependency.

### Privacy

`WH_KEYBOARD_LL` sees every keystroke on the system, including passwords and tokens. The callback's job is to be the privacy boundary:

1. Reads `VkCode` from `KBDLLHOOKSTRUCT`.
2. Categorizes it into one of `Modifier / System / TextLike / Navigation / Function / Other` — a pure function in [src/Input/KeyCategorizer.cs](src/Input/KeyCategorizer.cs).
3. Decides which (if any) semantic event to post: `InputKeyDown` for any keydown, `InputAltTabReleased` for Tab released while Alt held, `InputSystemKeyReleased` for Win/Esc/Print/F-keys-with-mod released.
4. **Discards `VkCode`.** The local variable goes out of scope. The pipeline only sees the semantic kind — nothing about which specific key was pressed.

The schema fields that touch keyboard activity are limited to `key_age_ms` and `idle_time_ms` — millisecond deltas, no key identity. Audited; verified end-to-end.

The modifier latches (`Alt/Ctrl/Shift/Win down`) live as private statics on the keyboard hook — they're needed for categorization (e.g., `F12 alone` is Function but `F12 with Shift` is System) and for Alt+Tab detection, and they never escape the keyboard hook either.

### Pipeline-pressure events

When the BufferBlock crosses 90% full (or drops back below 70% with hysteresis), the enricher prepends a synthetic event to its next output. That event flows through the pipeline like everything else and emerges as a row in every exporter with `classification=PIPELINE_PRESSURE`, `monitored_via=INTERNAL`, and a `note` like `"buffer pressure: 940/1024 (91%)"`. Its position in the seq-order tells the analyst exactly when the pressure built up. Detected on the dequeue side so the pressure event itself doesn't compete for buffer space with the events causing the pressure.

---

## Output formats

One file per UTC day per enabled format, in the log directory:

| Format | Filename | When | Notes |
|---|---|---|---|
| **CSV** | `spawnspotter-YYYY-MM-DD.csv` | append per event | RFC 4180. Header row on file create. Excel / Sheets friendly. |
| **JSONL** | `spawnspotter-YYYY-MM-DD.jsonl` | append per event | One JSON object per line. **Lossless** — full image paths per chain node, cwd always, env when `--capture-env`. `tail -f`-friendly. |
| **logfmt** | `spawnspotter-YYYY-MM-DD.logfmt` | append per event | `key=value` whitespace-separated; values with spaces or `=` are quoted. grep/awk-friendly. |
| **Markdown** | `spawnspotter-YYYY-MM-DD.md` | append per event | Table format. Pipes in titles escaped as `\|`. |
| **Plain text** | `spawnspotter-YYYY-MM-DD.log` | append per event | One-line pretty: `14:18:02.123Z [STEAL] pid=1234 cmd.exe ◄ Code.exe (window: "PowerShell")`. |
| **HTML report** | `spawnspotter-YYYY-MM-DD.html` | **only on graceful shutdown** | Self-contained file; sortable / filterable table; expandable rows with full chain. |

Default is `csv,jsonl`. Opt-in to more via `--format csv,jsonl,logfmt,md,log,html`.

### Record schema

| Field | Type | Notes |
|---|---|---|
| `timestamp_utc` | ISO 8601 (ms precision) | The OS-recorded time of the event (from `KBDLLHOOKSTRUCT.time` / `MSLLHOOKSTRUCT.time` / `dwmsEventTime`), reconstructed to a full 64-bit timestamp. |
| `classification` | enum | `STEAL` / `SESSION_LOCK` / `USER_ALT_TAB` / `USER_CLICK` / `USER_OTHER` / `PIPELINE_PRESSURE` |
| `monitored_via` | enum | `EVENT_SYSTEM_FOREGROUND` / `EVENT_OBJECT_SHOW` / `EVENT_OBJECT_FOCUS` / `INTERNAL` (for `PIPELINE_PRESSURE` rows) |
| `hwnd` | hex string | New foreground / shown / focused window handle. |
| `window_class` | string | Win32 window class name. |
| `window_title` | string | Window caption. |
| `focused_pid` | int | PID owning that HWND. |
| `parent_chain` | line-formats: `pid:basename:cmdline ► pid:basename:cmdline ► …` · JSONL: array of `{pid, image_path, basename, command_line, cwd, package_aumi?, env?, note?}` | Walks up to PID 0/4 or `--max-chain-depth`. JSONL is structured; other formats are basename-only for readability. |
| `key_age_ms` | int | Ms since last keyboard event. |
| `mouse_age_ms` | int | Ms since last mouse-button-down. |
| `idle_time_ms` | int | `min(key_age_ms, mouse_age_ms)`. |
| `locked_hwnd_before` | hex string | The HWND that was "what the user was really working on" before this event, or `0x0` if expired / destroyed. |
| `locked_pid_before` | int | PID for `locked_hwnd_before`. |
| `note` | string | Free-text annotation (`"parent already exited"`, `"locked anchor expired"`, `"monitor topology change"`, `"buffer pressure: 940/1024 (91%)"`, etc.). |

---

## Building from source

Requirements:

- Windows 10 / 11 x64
- .NET 10 SDK (`dotnet --list-sdks` should show 10.0.x)
- Visual Studio Build Tools (for the AOT linker — `link.exe`). Any recent VS install with C++ build tools works.

```powershell
# Tests
dotnet test

# Debug build (fast, AOT not exercised — useful for iteration)
dotnet build

# Production: Native AOT, single-file, self-contained ~11 MB binary
dotnet publish ./SpawnSpotter.csproj -c Release -r win-x64
# -> bin\Release\net10.0\win-x64\publish\SpawnSpotter.exe
```

The AOT publish step requires Visual Studio Build Tools on PATH for `vswhere.exe` and `link.exe`. If you see `'vswhere.exe' is not recognized`, prepend `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH for that shell.

---

## Project layout

```
SpawnSpotter/
├── plan.md                          full design spec (691 lines)
├── SpawnSpotter.csproj              main project (PublishAot=true)
├── Directory.Packages.props         central package management
├── global.json                      pins .NET 10 SDK
├── Program.cs                       entry point + CLI registration
├── src/
│   ├── Cli/                         Spectre.Console.Cli commands + settings + duration converter
│   ├── Classifier/                  pure classifier + glob matcher + truth-table inputs/outputs
│   ├── Events/                      Classification (incl. PipelinePressure), MonitoredVia, EventRecord schema
│   ├── Export/                      6 exporters + canonical EventRecord encoders + JSON source-gen
│   ├── Hooks/                       HookHostThread (single producer STA), MouseHook, KeyboardHook, WinEventHooks
│   ├── Input/                       KeyCategorizer (pure: vkCode -> KeyCategory)
│   ├── Lifecycle/                   Runner: wires the whole thing (including BroadcastBlock fan-out)
│   ├── Native/                      Win32.cs ([LibraryImport] P/Invoke), Win32Types.cs (structs)
│   ├── Pipeline/                    EventBus (hook post entry), RawHookEvent, EnrichedEvent,
│   │                                 EnrichmentPipeline (TransformManyBlock + BroadcastBlock), Counters
│   ├── Process/                     ProcessReader (NT API + RPM PEB walker), ProcessSnapshot
│   └── Ui/                          ConsoleUx (verbosity logic), StatusLine
└── tests/
    └── SpawnSpotter.Tests/          TUnit; 153 tests covering classifier truth-table, key
                                      categorizer, glob matcher, duration converter, exporter
                                      formats, HTML report
```

---

## Reference

The full design spec is in [plan.md](plan.md). It contains the problem statement, all functional/non-functional requirements, the architecture rationale, an exhaustive evaluation of ~25 existing tools that were considered, the implementation order, risk analysis, and the decisions log.

Sections of the plan that have been **superseded** by later refactors:

- §4 / §5.1: single STA thread for all hooks — went through "one per hook" (zero cross-hook contention) and is now back to **a single producer thread** for all five hooks. Sub-µs callbacks make per-hook isolation unnecessary; a single producer gives strict seq-order at ingress, eliminating the inter-thread race on `Interlocked.Increment` and removing any need for a downstream reorder buffer.
- §4 / §5.2: synchronous in-callback parent snapshot — moved into the parallel enrichment stage of the Dataflow pipeline in [src/Pipeline/EnrichmentPipeline.cs](src/Pipeline/EnrichmentPipeline.cs).
- §5.6 `Thread.Sleep(10)` retry inside `ReadProcessMemory` — removed; the retry is now instant or skipped.
- §5.3 keyboard/mouse hooks updating a global `InputState` struct — replaced by **unified pipeline**: every hook (input + window) posts via `EventBus.Post` to a single `BufferBlock`. Last-X timestamps now live as sink-local fields inside the classifier `ActionBlock`. There is no longer any global shared state between hooks and classifier.
- Classifier exporting via a synchronous callback — replaced by a **`BroadcastBlock<EventRecord>` fan-out** with one `ActionBlock` per consumer (console UX, file exporters, HTML accumulator, shutdown watcher). Each consumer has its own back-pressure boundary.
- Hook event timestamps from `Environment.TickCount64` at callback time — switched to **OS-recorded event time** from the hook data structs (`KBDLLHOOKSTRUCT.time` / `MSLLHOOKSTRUCT.time` / `dwmsEventTime`), reconstructed to a full 64-bit timestamp.

Everything else in the plan (privacy model, AOT discipline, classifier truth-table, record schema, CLI surface, exporter formats, exit codes) holds.
