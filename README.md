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

Two design properties drive everything:

1. **Hook callbacks must finish in microseconds.** Windows applies `LowLevelHooksTimeout` (default 300 ms) to `WH_KEYBOARD_LL` / `WH_MOUSE_LL`; an unresponsive hook makes the entire mouse feel sluggish. So all real work runs after the callback returns, off the hook thread.
2. **Hooks must be owned by a thread with a message pump.** `SetWindowsHookEx` and `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT` dispatch their callbacks via the installing thread's `GetMessage` loop. No pump = no callbacks fire.

### Five hook threads, one per hook

Each of the five hooks runs on its own dedicated STA thread with its own message pump. A burst of events from one hook (e.g. `EVENT_OBJECT_SHOW` floods during popup activity) can never queue another hook's callbacks behind it.

```
+---------------------------+
| SpawnSpotter.Mouse        |  WH_MOUSE_LL
| (STA + GetMessage loop)   |  writes InputState.LastMouseDownTickMs
+---------------------------+

+---------------------------+
| SpawnSpotter.Keyboard     |  WH_KEYBOARD_LL
| (STA + GetMessage loop)   |  categorizes vkCode -> KeyCategory (privacy: vkCode
+---------------------------+  discarded immediately) and writes timestamps

+---------------------------+
| SpawnSpotter.Foreground   |  EVENT_SYSTEM_FOREGROUND
| (STA + GetMessage loop +  |  hidden HWND: WM_DISPLAYCHANGE / WM_DPICHANGED
|  hidden HWND for monitor  |  --> writes MonitorSuppressUntilTickMs
|  topology suppression)    |
+---------------------------+

+---------------------------+
| SpawnSpotter.Show         |  EVENT_OBJECT_SHOW (filtered in-callback to
| (STA + GetMessage loop)   |  top-level visible non-owned-popup windows)
+---------------------------+

+---------------------------+
| SpawnSpotter.Focus        |  EVENT_OBJECT_FOCUS (same in-callback filter)
| (STA + GetMessage loop)   |
+---------------------------+
```

WH_MOUSE_LL and WH_KEYBOARD_LL only write timestamps to a lock-free `InputState` struct (read by the classifier). The three WinEvent hooks build a small `readonly struct RawHookEvent` (sequence#, tickMs, wall-clock UTC, hwnd, eventType) and post it to a shared `BufferBlock` via the Dataflow pipeline. Each callback returns in under a few microseconds.

### Three-stage Dataflow pipeline

WinEvent hook callbacks hand off to a TPL Dataflow pipeline whose stages run on the thread pool:

```
        +----------------------------------+
        | BufferBlock<RawHookEvent>        |
        | BoundedCapacity = 1024           |
        | drop-on-full (counter increments)|
        +----------------------------------+
                        |
                        v
        +------------------------------------------+
        | TransformBlock<RawHookEvent, EnrichedEvent>|
        |   MaxDegreeOfParallelism = N             |
        |     (default max(2, ProcessorCount/4))   |
        |   EnsureOrdered = true                   |
        |   BoundedCapacity = 1024                 |
        |                                          |
        | Per event:                               |
        |   GetWindowThreadProcessId               |
        |   GetClassNameW / GetWindowTextW         |
        |   ProcessReader.TrySnapshot (focused PID)|
        |   parent + ancestor chain walk           |
        |     via NtQueryInformationProcess +      |
        |     ReadProcessMemory PEB walk           |
        +------------------------------------------+
                        |
              in original Seq# order
                        |
                        v
        +------------------------------------------+
        | ActionBlock<EnrichedEvent>               |
        |   MaxDegreeOfParallelism = 1             |
        |   EnsureOrdered = true                   |
        |                                          |
        | Per event (single-threaded, no locking): |
        |   Cross-source dedupe (hwnd+tickMs)      |
        |   Classifier pipeline:                   |
        |     SESSION_LOCK override                |
        |     monitor topology suppression         |
        |     --ignore-class / --ignore-image      |
        |     threshold classification             |
        |       (USER_ALT_TAB / USER_CLICK /       |
        |        USER_OTHER / STEAL)               |
        |   LockedHwnd anchor bookkeeping          |
        |   Fan-out to enabled exporters           |
        +------------------------------------------+
                        |
                        v
                  exporters
```

Stage 2 runs in parallel — multiple workers enrich events concurrently — but `EnsureOrdered = true` re-serializes the output back into original Seq# order before stage 3, so the classifier's state machine (dedupe, LockedHwnd updates) sees events in their real-time order.

### Native AOT

`PublishAot=true`. All P/Invoke uses `[LibraryImport]` source-generated marshaling; hook callbacks are `static partial` methods marked `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]` with addresses passed via the `&Callback` operator — no managed delegates, no `Marshal.GetFunctionPointerForDelegate`, no `GCHandle.Alloc` pinning. JSON output uses a source-generated `JsonSerializerContext`. The shipped binary is a single ~11 MB .exe with no .NET runtime dependency.

### Privacy

`WH_KEYBOARD_LL` sees every keystroke on the system, including passwords and tokens. The hook callback:

1. Reads `VkCode` from `KBDLLHOOKSTRUCT`.
2. Categorizes it into one of `Modifier / System / TextLike / Navigation / Function / Other` — a pure function in [src/Input/KeyCategorizer.cs](src/Input/KeyCategorizer.cs).
3. **Discards `VkCode`.** The local variable goes out of scope. It never reaches a field, log line, exporter, console, or file.

The schema fields that touch keyboard activity are limited to `key_age_ms` and `idle_time_ms` — millisecond deltas, no key identity. Audited; verified end-to-end.

Verbosity 3 ("raw event stream") is documented to emit categories only — never key contents.

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
| `timestamp_utc` | ISO 8601 (ms precision) | When the hook callback timestamped this event. |
| `classification` | enum | `STEAL` / `SESSION_LOCK` / `USER_ALT_TAB` / `USER_CLICK` / `USER_OTHER` |
| `monitored_via` | enum | `EVENT_SYSTEM_FOREGROUND` / `EVENT_OBJECT_SHOW` / `EVENT_OBJECT_FOCUS` |
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
| `note` | string | Free-text annotation (`"parent already exited"`, `"locked anchor expired"`, `"monitor topology change"`, etc.). |

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
│   ├── Events/                      Classification, MonitoredVia, EventRecord schema
│   ├── Export/                      6 exporters + canonical EventRecord encoders + JSON source-gen
│   ├── Hooks/                       HookHostThread, MouseHook, KeyboardHook, WinEventHooks
│   ├── Input/                       KeyCategorizer, InputState (lock-free shared input state)
│   ├── Lifecycle/                   Runner: wires the whole thing together
│   ├── Native/                      Win32.cs ([LibraryImport] P/Invoke), Win32Types.cs (structs)
│   ├── Pipeline/                    RawHookEvent, EnrichedEvent, EnrichmentPipeline, Counters
│   ├── Process/                     ProcessReader (NT API + RPM PEB walker), ProcessSnapshot
│   └── Ui/                          ConsoleUx (verbosity logic), StatusLine (Spectre Live)
└── tests/
    └── SpawnSpotter.Tests/          TUnit; 153 tests covering classifier truth-table, key
                                      categorizer, glob matcher, duration converter, exporter
                                      formats, HTML report
```

---

## Reference

The full design spec is in [plan.md](plan.md). It contains the problem statement, all functional/non-functional requirements, the architecture rationale, an exhaustive evaluation of ~25 existing tools that were considered, the implementation order, risk analysis, and the decisions log.

Sections of the plan that have been **superseded** by later refactors:

- §4 / §5.1: single STA thread for all hooks — replaced by one STA thread per hook in [src/Hooks/HookHostThread.cs](src/Hooks/HookHostThread.cs).
- §4 / §5.2: synchronous in-callback parent snapshot — moved into the parallel enrichment stage of the Dataflow pipeline in [src/Pipeline/EnrichmentPipeline.cs](src/Pipeline/EnrichmentPipeline.cs).
- §5.6 `Thread.Sleep(10)` retry inside `ReadProcessMemory` — removed; the retry is now instant or skipped.

Everything else in the plan (privacy model, AOT discipline, classifier pipeline, record schema, CLI surface, exporter formats, exit codes) holds.
