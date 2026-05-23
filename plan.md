# Plan: Involuntary-Focus-Change Logger for Windows 11

**Target project type:** C# .NET 10 console application (latest stable SDK as of May 2026 — .NET 10 GA was November 2025).
**Project name:** `SpawnSpotter` (repo, exe, and root namespace).
**Repository status:** new repository, already created at `c:\Source\SixFive7\SpawnSpotter`.

This document is the complete specification for an agent that will scaffold and build the project. It contains the problem statement, all requirements, architectural decisions, the implementation plan, the full list of existing tools that were evaluated, and the reference source code to read for each component.

---

## 1. Why this tool exists

The user (Windows 11 Pro 10.0.26200, standard user — **NOT a local administrator**) experiences brief console-window flashes every few minutes that steal keyboard focus for under a second. The flashes corrupt text being typed in the previously focused window (letters land in or are eaten by the flashing window). The flashes are caused by short-lived processes that briefly create a visible console (cmd.exe / conhost.exe / docker.exe / node.exe) and then exit.

A prior investigation identified two probable causes:

1. **Claude Code's Playwright MCP servers.** Five `.mcp.json` files under `C:\Source\SixFive7\` launch `npx -y @playwright/mcp@latest …`. Claude Code wraps the command in `cmd /d /s /c` *without* `windowsHide`/`CREATE_NO_WINDOW`, so every restart flashes `cmd.exe` + `conhost.exe`. Known upstream issues anthropics/claude-code #14828 and #21375; unfixed.
2. **The VS Code Dev Containers extension** (`ms-vscode-remote.remote-containers`) polls the Docker engine by spawning short-lived `docker.exe` processes from every VS Code window even when no `devcontainer.json` is present.

A third candidate (Wispr Flow's profiling worker spawning `powershell.exe` every 180 s) was ruled likely-not-visible because both call sites pass `windowsHide:true`.

The user wants a **logging tool**, not a blocker. The goal is to:
- Confirm or refute the two suspects.
- Discover any additional culprits.
- Produce evidence (PID + parent-process chain + command line) that can be used to file precise bug reports or change configuration.

**Why no existing tool was chosen:** ~25 candidates were evaluated (full list in Appendix A). None satisfy all four hard constraints simultaneously:
- Catches sub-second window appearances (event-driven, not polling-based).
- Records the **full parent process chain** with command lines.
- Runs under standard user rights (no admin install or kernel driver).
- Filters out user-initiated focus changes (Alt+Tab, mouse click) so the log contains only involuntary "steal" events.

The closest existing tools — JocysCom/FocusLogger, ActivityWatch, Selfspy — each miss at least two of these. The "input-source filter" requirement is the one no existing tool addresses; this is the primary justification for a custom build.

---

## 2. Functional requirements

### MUST

- **F1.** Detect every foreground window change with sub-100 ms latency. Event-driven via the Win32 `SetWinEventHook` API, never polling.
- **F2.** Classify each foreground change as one of:
  - **USER_ALT_TAB** — preceded by an Alt+Tab key combination released within the last 500 ms.
  - **USER_CLICK** — preceded by a mouse-button-down event within the last 500 ms.
  - **USER_OTHER** — preceded by a hotkey-shaped event (Win key, Alt+Esc, Ctrl+Esc, etc.) within the last 500 ms.
  - **STEAL** — none of the above; no recent user input that would explain the change.
- **F3.** For every STEAL event, capture and log:
  - UTC timestamp (millisecond precision).
  - HWND, window class, window title of the new foreground window.
  - PID of the new foreground window's owning thread.
  - **Full parent process chain** walking up to `explorer.exe`, PID 4 (System), or PID 0. Each ancestor row contains: PID, full image path, command line.
  - Milliseconds since last keyboard event, milliseconds since last mouse event.
  - HWND + PID of the "locked" window (the user's actual working window) before the steal.
- **F4.** Persist the log as **append-only CSV** to a dated file (one file per UTC day). Append-only means crash-safe: a power loss or kill leaves prior events intact.
- **F5.** Run unattended for 24+ hours.
- **F6.** Require no administrative privileges to install or run. The binary must run as the current standard user.
- **F7.** Idle resource budget: < 10 MB RAM, < 0.1 % CPU when no focus changes are happening.

### SHOULD

- **S1.** Minimal external dependencies — at most one NuGet package, and only if it materially reduces P/Invoke surface area.
- **S2.** No GUI. A hidden or no-console mode is fine; default to a small console showing "running, N events logged, last event at HH:MM:SS" updating in place.
- **S3.** Graceful shutdown on Ctrl+C / console close / SIGTERM equivalent — flush buffers, release hooks, exit cleanly.
- **S4.** Self-contained publish should be possible (`dotnet publish -r win-x64 --self-contained`) so the user can drop a single .exe on the desktop.
- **S5.** Distinguish privacy categories of keyboard events. The keyboard hook MUST NOT log key contents or correlate keys to typed text. Only timestamps and high-level categories (modifier / system / text-like / navigation) are stored.

### OUT OF SCOPE (v1)

- Active blocking or restoration of focus changes (the user wants a logger, not a defender; no `--auto-restore`).
- Live GUI dashboard.
- SQLite or other database storage.
- Service installation / Task Scheduler autostart (user can configure manually after v1 is proven).
- Logging keystroke contents.
- Multi-monitor per-monitor focus tracking beyond what `GetForegroundWindow` reports (we DO suppress noise from DPI / resolution changes — see §5.5 — but we don't model per-monitor focus state separately).
- Network / IPC (no streaming HTTP/SSE endpoint, no remote log shipping).
- Two-process / sentinel-and-consumer architecture.
- Bug-report-generator subcommands (e.g. tailored anthropic-issue markdown output).
- Replay / re-classification of a stored JSONL with different thresholds.
- Coalesced "burst" rollup of repeated STEALs into single rows.
- Toast notifications on STEAL.
- Heartbeat rows for liveness detection.
- Polling fallback as belt-and-suspenders alongside the WinEvent hooks.

**Note:** `EVENT_OBJECT_SHOW` and `EVENT_OBJECT_FOCUS` were originally deferred to v1.1 but have been **pulled forward into v1.0** per the 2026-05-24 refinement — see §5.2.

---

## 3. Project setup

- **SDK:** .NET 10 (GA Nov 2025). `TargetFramework: net10.0`. C# language version: 14 (default for net10.0).
- **Project type:** Console executable, `OutputType: Exe`.
- **Platform:** `win-x64` only. Windows-specific via Win32 APIs.
- **Publish target — Native AOT from day 1.** Project enables `PublishAot=true`, `InvariantGlobalization=true`, `IlcOptimizationPreference=Size`. Single-file output; no .NET runtime install needed on the target machine. **AOT-mandatory rules:**
  - All P/Invoke uses `[LibraryImport]` source-generated marshaling — **never** `[DllImport]`.
  - Hook callback methods are `static partial` and marked `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]`. (`CallConvStdcall` is a no-op on x64 where stdcall coincides with the default ABI, but it is required for correctness on x86 and is harmless on all platforms — always specify it.)
  - Callback addresses are passed as function pointers via the `&Callback` operator. **Never** via `Marshal.GetFunctionPointerForDelegate` (AOT-incompatible) and **never** via a managed `delegate` field. Because no managed delegate ever exists, no `GCHandle.Alloc` pinning is required and the "GC collects the hook delegate" trap that plagues classic `[DllImport]`-based C# hook code is structurally impossible on this path.
  - No reflection except where the framework provides annotation-safe paths.
  - Verify by running `dotnet publish -c Release -r win-x64` on every commit — debug builds will NOT catch AOT regressions.
- **Solution layout:** Single project at repo root (`SpawnSpotter.csproj` next to `plan.md`); test project at `/tests/SpawnSpotter.Tests/`. README at repo root with build/run instructions.
- **Project file conventions:** SDK-style csproj, implicit usings, nullable enabled, latest language version. Central package management via `Directory.Packages.props`. Use C# 14 features (collection expressions, primary constructors, the `field` keyword, params collections, extension members, etc.) where they meaningfully improve readability.
- **Dependencies:**
  - `Spectre.Console.Cli` — CLI parsing + auto-generated `--help` output. AOT-compatible. Drives all command-line behavior and the help text users/agents will read.
  - `Spectre.Console` — live status line via the `Live`/`Status` APIs (transitive from `Spectre.Console.Cli`).
  - `TUnit` (test project only) — modern source-generator-based test framework, AOT-aligned, fast cold-start, no reflection-based discovery.
  - All hooks, NT API calls, message loop, channels, file I/O: raw P/Invoke + BCL only. **No `MouseKeyHook` (gmamaladze/globalmousekeyhook) and no `SharpHook` (TolikPylypchuk).** Both were audited on 2026-05-24 and rejected:
    - `MouseKeyHook` is unmaintained (last NuGet release 5.7.1 on 2023-04-10; open issues from 2024–2026 unanswered) and AOT-incompatible (uses `[DllImport]` + a managed `HookProcedure` delegate marshaled via `Marshal.GetFunctionPointerForDelegate`); it also drags in `System.Windows.Forms` as a transitive dependency via `KeyEventArgsExt : System.Windows.Forms.KeyEventArgs`, which would force the project off pure `net10.0` onto `Microsoft.WindowsDesktop.App`.
    - `SharpHook` IS AOT-compatible and actively maintained, but bundles `libuiohook` as a native dependency (~50 KB extra payload, plus a cross-platform code path we don't need on Windows-only).
    - Hand-rolled `[LibraryImport]` + `[UnmanagedCallersOnly]` low-level hook P/Invoke is ~80 lines, AOT-clean by construction, has zero third-party native footprint, and keeps the privacy surface (we never materialize `vkCode` into managed memory in the first place) fully auditable.
- **Forbidden:** `System.Management` (WMI), `Microsoft.Diagnostics.Tracing.TraceEvent`, anything requiring admin or kernel-mode drivers, anything that does network calls, `[DllImport]` (must use `[LibraryImport]`), instance-method hook callbacks (must be `static` + `[UnmanagedCallersOnly]`).
- **Source control:** standard `.gitignore` for VS / Rider / .NET; `bin/`, `obj/`, `*.user`, `.vs/`, `publish/` excluded.

---

## 4. Architecture overview

Single-process console application. One STA thread runs a Windows message loop, hosts a hidden top-level window, and owns **five** hooks (three WinEvent hooks + two low-level input hooks). A background consumer task drains a bounded channel of events, classifies them, walks parent process chains, and writes records to all enabled exporters from a single canonical `EventRecord` value. Layout:

```
[ STA main thread (message loop + hidden HWND) ]
        │
        ├── SetWinEventHook(EVENT_SYSTEM_FOREGROUND) ───► foreground-change callback ──┐
        ├── SetWinEventHook(EVENT_OBJECT_SHOW)        ───► top-level-show callback ────┤
        ├── SetWinEventHook(EVENT_OBJECT_FOCUS)       ───► focus-change callback ──────┤
        ├── SetWindowsHookEx(WH_KEYBOARD_LL)          ───► keyboard callback ──────────┤
        ├── SetWindowsHookEx(WH_MOUSE_LL)             ───► mouse callback ─────────────┤
        └── hidden HWND WndProc                       ───► WM_DISPLAYCHANGE / WM_DPICHANGED
                                                              (sets MonitorSuppressUntil) ─┤
                                                                                            │
                                                                          (in-callback)
                                                                          • timestamp
                                                                          • category / monitored_via
                                                                          • HWND / PID
                                                                          • synchronous PID+image+cmdline+cwd
                                                                            snapshot of focused + immediate parent
                                                                                            │
                                                                                            ▼
                                                                          Channel<RawEvent>
                                                                                            │
                                                                                            ▼
[ background consumer task ] ── classify ── walk parent chain ── encode EventRecord ──► [ exporters: JSONL · CSV · logfmt · md · log · html-on-shutdown ]
```

**Why this split:** low-level Windows hook procedures must return within ~300 ms or Windows silently unloads the hook. Walking a full parent chain via `NtQueryInformationProcess`, reading process PEBs via `ReadProcessMemory`, and writing six output formats is too slow to do inside the hook. Hook callbacks must only timestamp, categorize, capture the focused-process + immediate-parent snapshot, and enqueue.

**Why STA thread + message loop:** `WH_KEYBOARD_LL` and `WH_MOUSE_LL` deliver events via the Windows message queue. The hook procedure runs on the thread that installed the hook. The thread must have an active message loop for the hooks to fire at all. **We hand-roll a `GetMessage`/`TranslateMessage`/`DispatchMessage` loop** (~30 lines of `[LibraryImport]` P/Invoke) rather than depend on `Application.Run()` from `System.Windows.Forms` — this keeps the TFM at pure `net10.0` and avoids the `Microsoft.WindowsDesktop.App` shared framework dependency. `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT` flag does NOT require a message loop on the installing thread for hooks of the right scope, but using the same STA thread + message loop for all hooks keeps things simple.

**Why a hidden top-level HWND:** `WM_DISPLAYCHANGE` is broadcast to top-level windows only; message-only windows (`HWND_MESSAGE`) do NOT receive broadcast messages. To subscribe to monitor topology changes (display resolution, monitor add/remove via `WM_DISPLAYCHANGE`) and DPI changes (`WM_DPICHANGED`), the STA thread creates a single hidden window via `CreateWindowExW` (style `WS_OVERLAPPED`, sized 0×0, never `ShowWindow`'d). Its `WndProc` is a static `[UnmanagedCallersOnly]` function that handles those two messages by writing a shared `MonitorSuppressUntil = Environment.TickCount64 + 5000` timestamp, then defers everything else to `DefWindowProcW`. The classifier consults `MonitorSuppressUntil` (see §5.5) to avoid reporting docking/undocking-induced focus cycles as STEAL.

---

## 5. Components

### 5.1 Hook installer

- One static class. `Start()` creates the hidden top-level window, installs all hooks, and starts the message-loop pump. `Dispose()` unhooks everything, destroys the window, and exits the pump.
- Hook callbacks are `static partial` methods marked `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]`. Under Native AOT they compile to direct native entry points. Their addresses are obtained via the `&Callback` operator and passed to `SetWinEventHook` / `SetWindowsHookEx` / `CreateWindowExW` (`WNDCLASSEXW.lpfnWndProc`) as function pointers. **No managed `delegate` instance exists, no `Marshal.GetFunctionPointerForDelegate` call exists, and no `GCHandle.Alloc(...)` pinning is required.** The "GC collects the hook delegate" trap that plagues classic `[DllImport]`-based C# hook code is structurally impossible on this path — there is no managed delegate that could be collected.
- The hook handles returned by the install calls (`HWINEVENTHOOK` for `SetWinEventHook`, `HHOOK` for `SetWindowsHookEx`, `HWND` for the hidden window, `ATOM` for the registered window class) ARE stored in static fields so the corresponding teardown calls can find them at shutdown.
- On uninstall: `UnhookWinEvent` for the three WinEvent hooks, `UnhookWindowsHookEx` for the two low-level hooks, `DestroyWindow` + `UnregisterClassW` for the hidden window, then `PostQuitMessage(0)` to break the message loop. Failing to unhook is a leak but not catastrophic since the OS cleans up at process exit.

### 5.2 Window event sources (three WinEvent hooks)

Three `SetWinEventHook` registrations, all with flags `WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS` (run in our process; don't fire for our own events at the OS level), `idProcess=0`, `idThread=0` (system-wide):

| Event | Code | In-callback filter | Purpose |
|---|---|---|---|
| `EVENT_SYSTEM_FOREGROUND` | `0x0003` | none beyond the standard `idObject == OBJID_WINDOW` / `idChild == CHILDID_SELF` | Primary signal — foreground window changed |
| `EVENT_OBJECT_SHOW` | `0x8002` | `idObject == OBJID_WINDOW`, `idChild == CHILDID_SELF`, HWND has `WS_VISIBLE` set, `WS_CHILD` clear, `GetWindow(hwnd, GW_OWNER) == NULL` (top-level, non-owned-popup only) | Catches flashes that briefly become visible without ever stealing foreground (the user's primary symptom) |
| `EVENT_OBJECT_FOCUS` | `0x8005` | `idObject == OBJID_WINDOW`, `idChild == CHILDID_SELF` | Catches UWP / modal-dialog focus changes that don't fire `EVENT_SYSTEM_FOREGROUND` |

Each callback receives `(hWinEventHook, eventType, hwnd, idObject, idChild, dwEventThread, dwmsEventTime)`. We record `eventType` as `monitored_via` on the enqueued record (see §5.7 schema).

In each callback:
- Capture wall clock (`DateTime.UtcNow`) and tick count (`Environment.TickCount64`).
- For `EVENT_OBJECT_SHOW` / `EVENT_OBJECT_FOCUS`: apply the filters above before enqueueing. Drop early — these events fire frequently for tooltips, menus, transient popups; aggressive filtering is essential to stay under the hook-callback budget.
- Read `GetForegroundWindow()` defensively in case the parameter HWND lags, look up PID via `GetWindowThreadProcessId`, read window class via `GetClassNameW`, read window title via `GetWindowTextW`.
- **Synchronous parent snapshot:** before enqueueing, synchronously call NT APIs for BOTH the focused PID and its immediate parent PID to capture:
  - Parent PID (`NtQueryInformationProcess` class 0 `ProcessBasicInformation` — also yields `PebBaseAddress`).
  - Full image path (`QueryFullProcessImageNameW`).
  - Full command line (`NtQueryInformationProcess` class 60 `ProcessCommandLineInformation`).
  - Current working directory (read PEB → `RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath` via `ReadProcessMemory`).
  
  Store all of this in the enqueued struct. **Reason:** short-lived flashes (the primary target) may exit between the callback and the consumer's wakeup; the synchronous snapshot guarantees we capture image + cmdline + cwd for the focused process and its immediate parent even if they die within milliseconds. Cost: ~200–400 µs total (5–6 NT API calls + 1–2 RPMs). Hook-callback budget is ~1 ms — comfortable margin. Walking grandparent-and-up is deferred to the consumer (§5.6).
- Push the struct into the channel. Do NOT call `Process.GetProcessById` here — defer to the consumer.

**Cross-source dedupe:** the consumer dedupes by `hwnd` within the `--dedupe-window-ms` window (default 50 ms). If the same HWND is reported from multiple WinEvent sources within the window (common: a real foreground change generates both `EVENT_OBJECT_SHOW` and `EVENT_SYSTEM_FOREGROUND`), the first-seen record wins and subsequent ones are dropped. The `monitored_via` of the winning record is preserved as-is — we do NOT merge `monitored_via` into an array, keeping the schema flat.

### 5.3 Keyboard hook

- `SetWindowsHookEx(WH_KEYBOARD_LL, …, 0, 0)` — system-wide low-level hook.
- Callback structure: `KBDLLHOOKSTRUCT { vkCode, scanCode, flags, time, dwExtraInfo }`. The `flags` field's bit 7 (LLKHF_UP) tells you key-up vs key-down.
- For each event, update *one* thread-safe shared snapshot (`Interlocked.Exchange` on a struct, or a `volatile` reference to an immutable record):
  - `LastKeyTimestampMs` (ticks at this event).
  - `LastKeyCategory` enum: `Modifier | System | TextLike | Navigation | Function | Other`.
  - `AltDown`, `CtrlDown`, `ShiftDown`, `WinDown` — current modifier state, updated on every key event of those vkCodes.
  - `LastAltTabReleaseMs` — set to `now` when the Tab key is released while `AltDown` was true at the moment of the most recent Tab press.
  - `LastAltEscReleaseMs` — same for Alt+Esc, Alt+Shift+Tab, Win+Tab.
- **Privacy rule:** never store `vkCode` itself in any log row. Only categories. The categorization happens inside the hook callback and the raw vkCode is discarded.

Key category mapping (illustrative — finalize during implementation):
- **System** = VK_LWIN, VK_RWIN, VK_APPS, VK_ESCAPE, VK_PRINT, VK_SNAPSHOT, F1–F24 when combined with a modifier.
- **Modifier** = VK_SHIFT, VK_CONTROL, VK_MENU (Alt), VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, plus Caps/Num/Scroll lock.
- **TextLike** = A–Z, 0–9, OEM punctuation, VK_SPACE.
- **Navigation** = arrows, Home, End, PgUp, PgDn, Insert, Delete, Tab (when not combined with Alt), Backspace, Enter.
- **Function** = F1–F12 without modifier.

### 5.4 Mouse hook

- `SetWindowsHookEx(WH_MOUSE_LL, …, 0, 0)`.
- We only care about button-down events: `WM_LBUTTONDOWN`, `WM_RBUTTONDOWN`, `WM_MBUTTONDOWN`, `WM_XBUTTONDOWN`. Movement and wheel events are ignored.
- On each down event, record `LastMouseDownTimestampMs = now` and optionally `LastMouseDownPoint = (x, y)`. The point lets us later check whether the click landed inside the newly-focused window's rectangle (`WindowFromPoint`), which strengthens the USER_CLICK classification.
- Do not log click coordinates to the CSV. They're transient state for classification only.

### 5.5 Classifier (in consumer task)

Classification operates on every event from any of the three WinEvent sources. The pipeline runs in this order; the first matching step wins.

**Step 1 — Session-lock override.** If `image_path == \Windows\System32\LogonUI.exe` OR (`window_class in ["LockApp", "Windows.UI.Core.CoreWindow"]` AND the focused image is `Microsoft.LockApp_…\LockApp.exe`): classify as **`SESSION_LOCK`** and skip further classification. The row IS recorded at verbosity 0 (alongside STEAL) but counted in a separate bucket. **Do not** update `LockedHwnd`.

**Step 2 — Monitor topology suppression.** If `Environment.TickCount64 < MonitorSuppressUntil` (set by the hidden HWND's WndProc on `WM_DISPLAYCHANGE` or `WM_DPICHANGED`, with a 5-second window): classify as **`USER_OTHER`** with `note = "monitor topology change"`. **Do not** update `LockedHwnd`. (At default verbosity 0 these events are suppressed from output entirely; they appear at `-v 1+`.)

**Step 3 — Ignore filters.** If the new window's class matches any `--ignore-class <pattern>` glob OR the focused image basename matches any `--ignore-image <pattern>` glob: drop silently (no row written, no `LockedHwnd` update). At verbosity ≥ 2 the consumer emits a one-line "dropped by filter" diagnostic.

**Step 4 — Standard input-source classification.**

```
Δalt   = now - LastAltTabReleaseMs
Δother = now - LastOtherSystemKeyReleaseMs
Δclick = now - LastMouseDownMs

if Δalt   < threshold_alt_tab → USER_ALT_TAB
elif Δclick < threshold_click  → USER_CLICK
elif Δother < threshold_other  → USER_OTHER
else                            → STEAL
```

After classification:
- **`USER_*`**: update `LockedHwnd = newHwnd`, `LockedPid = newPid`, `LockedAt = now`. Row recorded only at verbosity ≥ 1.
- **`STEAL`**: record the row. Do not update `LockedHwnd`.
- **`SESSION_LOCK`**: record the row. Do not update `LockedHwnd`.

The `LockedHwnd` is the "what the user thinks is focused" anchor. It's not Win32-locking anything; it's a state variable for populating the log row's `locked_hwnd_before` / `locked_pid_before` fields.

**LockedHwnd robustness.** For the anchor to remain useful across multi-hour runs it must be either fresh or explicitly marked stale. The classifier enforces this on every event:

1. **`IsWindow` validation.** Before reading `LockedHwnd` to populate `locked_hwnd_before`, the consumer calls `IsWindow(LockedHwnd)`. If it returns false (the window was destroyed between the USER_* event and now): the output field is written as `0x0`, `locked_pid_before = 0`, with `note = "locked window destroyed"`, and the in-memory `LockedHwnd` is cleared.
2. **Idle TTL.** If `now - LockedAt > --locked-hwnd-ttl-min` (default 5 minutes; configurable; set to `0` to disable the timeout while keeping `IsWindow` validation): the output field is `0x0` with `note = "locked anchor expired (>N min idle)"`, and `LockedHwnd` is cleared. The TTL prevents a 2-hour-old anchor — possibly already destroyed but happening to still exist with a recycled HWND assigned to a different process — from appearing as fresh data in the log.
3. **Re-establish.** The next `USER_*` event after a clear sets `LockedHwnd` afresh. There is no attempt to restore a previous anchor.

**Startup initialization:** at tool launch, set `LockedHwnd = GetForegroundWindow()`, `LockedPid = GetWindowThreadProcessId(LockedHwnd)`, `LockedAt = now` so the very first STEAL event (if it happens before any USER_* event) has a sensible "what was focused before" value instead of zero / null.

**Tuning:** the 500 ms thresholds are starting values. If the log shows false STEAL classifications (e.g. clicking on a window that takes >500 ms to actually receive focus), widen to 750–1000 ms via `--threshold-ms` (or per-source overrides in §5.9).

### 5.6 Parent process chain walker

Done in the consumer task, not in the hook callback. Speed still matters because short-lived flashes may exit before the walker runs — but having a few hundred ms here is acceptable for typical "few seconds" lifetimes, and many flashes spawn longer-lived sibling processes whose parents we can still query.

For each PID in the chain:

1. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid)`. `PROCESS_VM_READ` is required for the `ReadProcessMemory` calls in steps 5–6. Returns `NULL` if the process has exited or access is denied (e.g. PPL-protected processes like `csrss`, `services`). If null: record `pid=N, image="<exited or access denied>"`, set `cmdline = null`, set `cwd = null`, and stop walking.
2. **Image path (full, absolute):** `QueryFullProcessImageNameW(hProcess, 0, buf, ref size)`. Works without admin for any process you can open with `PROCESS_QUERY_LIMITED_INFORMATION`.
3. **Command line:** `NtQueryInformationProcess(hProcess, ProcessCommandLineInformation /* class 60 */, &uniStr, sizeof(uniStr), &returnLength)`. Returns a `UNICODE_STRING` with the command line. Information class added in Windows 8.1; standard user can read it for processes opened with QUERY_LIMITED_INFORMATION.
4. **UWP `package_aumi` fallback:** if step 3 returned an empty command line, OR the image path is under `C:\Windows\SystemApps\` or `C:\Program Files\WindowsApps\`, call `GetApplicationUserModelId(hProcess, &len, buf)` from `kernel32.dll` to retrieve the AppUserModelID. Store as `package_aumi` on the chain node. UWP / Store apps generally have no useful classical cmdline; AUMI is the equivalent identifier (e.g. `Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`).
5. **Parent PID + PEB pointer:** `NtQueryInformationProcess(hProcess, ProcessBasicInformation /* class 0 */, &pbi, sizeof(pbi), &returnLength)`. `PROCESS_BASIC_INFORMATION` gives both `InheritedFromUniqueProcessId` (the parent PID — used to recurse) and `PebBaseAddress` (used in step 6).
6. **Working directory (always) + environment (opt-in):**
   - `ReadProcessMemory(hProcess, pebAddr, &peb, sizeof(PEB), out _)` → `peb.ProcessParameters` pointer.
   - `ReadProcessMemory(hProcess, peb.ProcessParameters, &rupp, sizeof(RTL_USER_PROCESS_PARAMETERS), out _)` → reveals `CurrentDirectory.DosPath` (a `UNICODE_STRING`) and (when `--capture-env` is set) `Environment` + `EnvironmentSize`.
   - `ReadProcessMemory` once more to copy the `DosPath.Buffer` bytes into our own buffer; decode as UTF-16 → `cwd` string. **`cwd` is always captured.**
   - When `--capture-env` is on: another `ReadProcessMemory` for `EnvironmentSize` bytes from `Environment`; parse as null-separated UTF-16 `KEY=VALUE` entries; store as a structured object on the chain node. (See §8 risks for the secrets warning.)
   - If any `ReadProcessMemory` fails (race with process exit, paged-out memory, WOW64 mismatch — see §8): retry once after a 10 ms sleep. On final failure populate `cwd = "<unavailable>"` with a `note` and proceed without aborting the walk.
   - **WOW64 awareness:** call `IsWow64Process2(hProcess, &processMachine, &nativeMachine)` once per process. If the target is 32-bit on a 64-bit OS, use the 32-bit PEB layout (`PEB32`, `RTL_USER_PROCESS_PARAMETERS32`, 32-bit pointers / `ULONG` lengths) instead of the 64-bit layout. The two layouts differ in field offsets and pointer width and CANNOT be read interchangeably.
7. Close handle. Recurse with the parent PID.

Stop conditions:
- Parent PID == 0 (System Idle Process)
- Parent PID == 4 (System)
- Parent PID == this current PID (cycle protection)
- `OpenProcess` returned NULL (process exited or access denied)
- Chain length > `--max-chain-depth` (default 20)

**There is no `explorer.exe` stop heuristic.** Some users replace the shell (Insider builds, certain corporate setups); some run with elevated explorer instances; and `explorer.exe` is occasionally a useful intermediate parent worth seeing in the chain. The remaining stop conditions are sufficient and never wrong.

**Chain rendering for line-oriented formats** (CSV, plain text, Markdown, logfmt) — a single field, pipe-separated, **basename only** for readability:

`pid:basename:cmdline_quoted ► pid:basename:cmdline_quoted ► …`

**Chain rendering for JSONL** — a structured array of objects, each with the full captured fields: `{ pid, image_path (absolute), basename, command_line, cwd, package_aumi?, env? }`. The HTML report reads back the JSONL on shutdown and surfaces full paths + cwd in expandable rows. (Full image paths and `cwd` in JSONL were pulled forward from a v1.5 deferral per the 2026-05-24 refinement — see §10 decisions #32, #33.)

### 5.7 Exporters (multi-format)

The logger writes to multiple format files in parallel — one file per format, per UTC day, in the configured log directory (default `%LOCALAPPDATA%\SpawnSpotter\logs\`). Every event is written to every enabled format.

**Canonical schema is JSONL.** Each event is materialized exactly once as a single immutable in-memory `EventRecord` value type; all enabled exporters encode their output from that same record. JSONL is the only format that emits the full structured parent chain (full image paths, `cwd`, optional `env`, `package_aumi`); line-oriented formats render the chain in a compact basename-only form. The HTML report (written on shutdown) reads back the day's JSONL file to produce expandable rows surfacing the full chain.

**Supported formats:**

| Format | Filename | Audience | Append? | Notes |
|---|---|---|---|---|
| **CSV** | `spawnspotter-YYYY-MM-DD.csv` | humans (Excel/Sheets) | yes | Header row written on file create. RFC 4180 escaping. |
| **JSONL** | `spawnspotter-YYYY-MM-DD.jsonl` | agents/scripts | yes | One JSON object per line; `tail -f` friendly. Parent chain is a structured array. |
| **logfmt** | `spawnspotter-YYYY-MM-DD.logfmt` | humans + grep/awk | yes | `key=value` whitespace-separated; double-quote values with spaces or special chars. |
| **Markdown** | `spawnspotter-YYYY-MM-DD.md` | human spot-reading | yes | Table header on file create, one row per event. Pipes in titles escaped as `\|`. Column widths drift over time — acceptable. |
| **Plain text** | `spawnspotter-YYYY-MM-DD.log` | human skim | yes | Pretty one-line format: `2026-05-23 14:18:02.123Z [STEAL] pid=1234 cmd.exe ◄ Code.exe (window: "Foo")`. |
| **HTML report** | `spawnspotter-YYYY-MM-DD.html` | human sharing | **no — written on shutdown** | Single self-contained file with embedded CSS/JS, sortable/filterable table. Re-read from JSONL at shutdown (or accumulated in memory). |

**Defaults:** CSV + JSONL enabled (`--format csv,jsonl`). Users opt in to more via `--format csv,jsonl,logfmt,md,log,html`.

**Common writer behavior:**
- Open with `FileShare.Read | FileShare.Delete`, mode `Append`. Append-only means crash-safe.
- Headers (CSV/Markdown) only written when the file is newly created (zero bytes).
- Flush after every event AND every 5 seconds AND on graceful shutdown.
- No size cap, no in-day rotation — daily file rotation alone is sufficient (expected volume <15 MB/day total across all formats even in pathological cases).
- Each exporter implements a common `IEventExporter` interface so adding/removing formats is trivial.

**Record schema (identical fields across formats — only encoding differs):**

| Field | Type | Description |
|---|---|---|
| `timestamp_utc` | ISO 8601 string (ms precision) | When the window event was timestamped in the hook callback |
| `classification` | enum | `STEAL` / `SESSION_LOCK` / `USER_ALT_TAB` / `USER_CLICK` / `USER_OTHER` |
| `monitored_via` | enum | `EVENT_SYSTEM_FOREGROUND` / `EVENT_OBJECT_SHOW` / `EVENT_OBJECT_FOCUS` — which WinEvent source produced this row |
| `hwnd` | hex string | New foreground / shown / focused HWND |
| `window_class` | string | Win32 window class name |
| `window_title` | string | Window title (often contains commas/quotes/newlines — escape per format) |
| `focused_pid` | int | The HWND's owning PID |
| `parent_chain` | string (line-oriented formats) OR array of objects (JSONL) | Line-oriented: `pid:basename:cmdline ► pid:basename:cmdline ► …`. JSONL: array of `{ pid, image_path, basename, command_line, cwd, package_aumi?, env? }` per node. Full image paths and `cwd` always present in JSONL; `env` only when `--capture-env` is set; `package_aumi` only when present for UWP processes |
| `key_age_ms` | int | ms since last keyboard event |
| `mouse_age_ms` | int | ms since last mouse-button-down |
| `idle_time_ms` | int | `min(key_age_ms, mouse_age_ms)` — ms since the last user input of any kind. Redundant but saves the analyst a per-row mental calculation |
| `locked_hwnd_before` | hex string | HWND the user was "locked" on before the event, or `0x0` if cleared by IsWindow-validation / idle-TTL. Updated on `USER_*` only; initialized at startup from `GetForegroundWindow()` |
| `locked_pid_before` | int | PID for `locked_hwnd_before`, or `0` when cleared |
| `note` | string | Free-text annotation. Examples: `"parent already exited"`, `"dedupe drop"`, `"locked window destroyed"`, `"locked anchor expired (>5 min idle)"`, `"monitor topology change"`, `"channel full"` |

### 5.8 Console output, verbosity, and run modes

Two orthogonal axes control console behavior — both configured via CLI flags (§5.9).

**Output mode (`--mode`):**

| Mode | Status line | Per-event console output | File logs | Use case |
|---|---|---|---|---|
| `interactive` (default) | yes (live, in-place) | yes (scrolling above status line) | yes | Human running the tool from a terminal |
| `silent` | no | no | yes | Agent invocation; headless / background |
| `status-only` | yes | no | yes | Long visible run without console scroll noise |

**Verbosity (`-v` / `--verbosity`, `0..3`):** controls what events appear in console AND in log files (verbosity filters at the source — events below the threshold are not recorded at all).

| Level | Emits |
|---|---|
| `0` (default) | `STEAL` + `SESSION_LOCK` events |
| `1` | level 0 + `USER_*` events with classification reason |
| `2` | level 1 + hook lifecycle (install/uninstall) + dedupe drops + `--ignore-class`/`--ignore-image` filter drops + classifier near-misses |
| `3` | level 2 + raw EVENT_SYSTEM_FOREGROUND / EVENT_OBJECT_SHOW / EVENT_OBJECT_FOCUS / keyboard / mouse event stream (key **categories** only — never key contents) |

**Status line rendering:** use Spectre.Console's `Live` API for in-place, multi-region updates. The status line stays anchored at the bottom; per-event lines scroll above it. Example status line:

`[SpawnSpotter] uptime 00:42:13 | STEAL 7  SESSION_LOCK 2  USER_ALT_TAB 24  USER_CLICK 91 | last steal 14:18:02 cmd.exe ◄ Code.exe | -v 1, --mode interactive | Ctrl+C to stop`

Per-event line example (verbosity ≥ 1, interactive mode):

`14:18:02.123Z STEAL  pid=1234  cmd.exe ◄ Code.exe ◄ explorer.exe  (window: "PowerShell")`

The console layout is itself self-documenting — a user or agent watching the output understands what the tool is doing without reading the README.

**Exit summary (all modes, including `silent`):** on graceful shutdown (Ctrl+C OR `--duration` expired), print a single-line summary to stdout regardless of `--mode`. "Silent" means "no live UI noise during the run", not "absolute silence on exit" — agents specifically want the final verdict.

Example:

`Ran 00:45:03. Logged STEAL=3 SESSION_LOCK=2 USER_ALT_TAB=12 USER_CLICK=44 USER_OTHER=1. Files: C:\Users\jori\AppData\Local\SpawnSpotter\logs\`

**Exit codes:**

| Code | Meaning |
|---|---|
| `0` | Graceful shutdown (Ctrl+C, `--duration` expired) |
| `1` | Hook installation failure or other startup error |
| `2` | Invalid CLI arguments (Spectre.Console.Cli default for parse errors) |
| other non-zero | Unhandled exception during run |

Agents can branch on the exit code without parsing stdout.

### 5.9 Configuration & CLI

CLI parsing via **Spectre.Console.Cli**. Commands are attribute-decorated classes. The `--help` output is auto-generated and follows conventional formatting (long flags, short aliases where useful, default values shown in help). Both humans and agents can parse the help output without reading external documentation — **the `--help` text is the canonical interface documentation**.

**Subcommand structure:**

| Invocation | Behavior |
|---|---|
| `spawnspotter` (no args) | Print top-level help listing available commands, then exit 0. |
| `spawnspotter watch [options]` | Start logging. This is the only "doing" command. |
| `spawnspotter version` | Print version + git commit and exit. |
| `spawnspotter watch --help` | Print full help for the watch command (all flags below). |

**`watch` flags:**

| Flag | Short | Default | Description |
|---|---|---|---|
| `--log-dir <path>` | | `%LOCALAPPDATA%\SpawnSpotter\logs\` | Output directory (created if missing) |
| `--format <list>` | `-f` | `csv,jsonl` | Comma-separated subset of `csv,jsonl,logfmt,md,log,html` |
| `--mode <name>` | `-m` | `interactive` | One of `interactive`, `silent`, `status-only` |
| `--duration <span>` | `-d` | (unset = run forever) | Auto-stop after this duration. Human-friendly format: `90s`, `45m`, `2h`, `1d`, compound `2h30m`. On expiration: graceful shutdown → flush exporters → write HTML report → print exit summary → exit 0. `0` and negatives rejected at parse time. Implemented via a custom Spectre `TypeConverter`. |
| `--max-steals <int>` | | (unset = unlimited) | Stop cleanly after N `STEAL` events have been logged. Same shutdown path as `--duration` expiration. Combines with `--duration` (whichever triggers first wins). Useful for agent runs that want to capture one representative STEAL and exit |
| `--verbosity <0..3>` | `-v` | `0` | See §5.8 |
| `--threshold-ms <int>` | | `500` | Classifier threshold across all input sources |
| `--threshold-alt-tab-ms <int>` | | (= `--threshold-ms`) | **Advanced override** for Alt+Tab only |
| `--threshold-click-ms <int>` | | (= `--threshold-ms`) | **Advanced override** for click only |
| `--threshold-other-ms <int>` | | (= `--threshold-ms`) | **Advanced override** for other-system only |
| `--dedupe-window-ms <int>` | | `50` | Same-HWND duplicate suppression window (across all three WinEvent sources) |
| `--max-chain-depth <int>` | | `20` | Parent-chain walker safety cap |
| `--ignore-class <pattern>` | | (none) | Glob pattern matched against the new window's class name. Matching events are dropped before logging. Repeatable (multiple `--ignore-class` flags are combined with OR) |
| `--ignore-image <pattern>` | | (none) | Glob pattern matched against the focused process's image basename. Matching events are dropped. Repeatable |
| `--locked-hwnd-ttl-min <int>` | | `5` | Minutes of no user input after which the `LockedHwnd` anchor is cleared. Set to `0` to disable the timeout (the per-event `IsWindow` validation still runs) |
| `--capture-env` | | `false` | Capture full per-process environment blocks (`KEY=VALUE` pairs) into the JSONL chain nodes. **WARNING:** env blocks frequently contain secrets (`GITHUB_TOKEN`, API keys, connection strings). Default off; enabling marks the JSONL files as secret-bearing artifacts |
| `--help` | `-h` | | Print this help |

**Design intent:**
- Running the binary with no flags shows help — the tool refuses to "guess" what the user wants and instead documents itself. Agents reading help output see every flag and default value spelled out.
- A single `--threshold-ms` is the sane default knob. The per-source override flags are present but de-emphasized — power users tuning false positives can split values; everyone else uses the single value.
- All defaults are visible in the `--help` output (Spectre.Console.Cli's `[Description]` + default-value rendering handles this). No out-of-band documentation needed.
- No config file — flags only. Repeatable invocations live in shell scripts / scheduled tasks, not in a config syntax the user has to learn.

---

## 6. Implementation order

Each step should be testable independently before moving on.

1. **Skeleton + AOT publish.** Console app, TFM `net10.0`, SDK-style csproj with implicit usings, nullable enabled, `PublishAot=true`, `InvariantGlobalization=true`. README stub. Wire Ctrl+C handler. Verify `dotnet publish -c Release -r win-x64` produces a single-file native binary <15 MB on an empty program before adding any P/Invoke. **AOT publish must succeed at every commit going forward.**
2. **CLI scaffold.** Add `Spectre.Console.Cli`. Define `watch` and `version` subcommands per §5.9. `watch` parses all flags with their defaults. Running the bare binary prints help and exits 0. Running `watch` with no extra flags begins a no-op loop that just sleeps and responds to Ctrl+C. Verify `--help` output renders correctly under AOT.
3. **TUnit test project.** Add `/tests/SpawnSpotter.Tests/`. Write one trivial passing test. Confirm the test project still runs cleanly; AOT-incompatible reflection in test infrastructure is acceptable so long as it stays in the test assembly only.
4. **Message loop + STA thread + hidden HWND.** Hand-roll the `GetMessage`/`TranslateMessage`/`DispatchMessage` loop using `[LibraryImport]`. Register a window class (`RegisterClassExW`) and create a hidden top-level window via `CreateWindowExW` (style `WS_OVERLAPPED`, size `0×0`, parent `HWND_DESKTOP`, never `ShowWindow`'d). Its WndProc is a static `[UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]` function that handles `WM_DISPLAYCHANGE` and `WM_DPICHANGED` by writing `MonitorSuppressUntil = Environment.TickCount64 + 5000`; all other messages fall through to `DefWindowProcW`. Install an empty `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` callback (also static `[UnmanagedCallersOnly]`) that emits `fg changed: hwnd=0xNNN`. Confirm: hook fires on Alt+Tab; does NOT fire on click-in-same-window; hidden HWND receives `WM_DISPLAYCHANGE` when display resolution changes.
5. **Foreground enrichment + additional WinEvent sources.** From the foreground callback (still in-callback for now), look up PID + window class + window title via `GetWindowThreadProcessId`, `GetClassNameW`, `GetWindowTextW`. Emit them. Additionally install `SetWinEventHook(EVENT_OBJECT_SHOW)` and `SetWinEventHook(EVENT_OBJECT_FOCUS)` with the in-callback filters described in §5.2 (top-level visible non-child non-owned-popup only). Tag each enqueued record with `monitored_via`. Verify: opening a tooltip fires `EVENT_OBJECT_SHOW` without `EVENT_SYSTEM_FOREGROUND`; a UWP app focus change may fire `EVENT_OBJECT_FOCUS` without `EVENT_SYSTEM_FOREGROUND`.
6. **Keyboard hook.** Install `WH_KEYBOARD_LL` (static `[UnmanagedCallersOnly]` callback). Emit each key event's **category** (never vkCode). Confirm Alt+Tab detected as Alt-down → Tab-down → Tab-up → Alt-up; `LastAltTabReleaseMs` set correctly. **Unit test** the category mapping in TUnit.
7. **Mouse hook.** Install `WH_MOUSE_LL`. Emit each mouse-button-down. Confirm clicks register.
8. **Classifier.** Wire the three timestamps into the foreground callback. Implement the full §5.5 pipeline: (1) SESSION_LOCK override for `LogonUI.exe` + `LockApp`; (2) `MonitorSuppressUntil` check → tag as `USER_OTHER` with note `"monitor topology change"`; (3) `--ignore-class` / `--ignore-image` glob drops; (4) standard input-source classification. Implement `LockedHwnd` robustness: `IsWindow` validation on every event, idle-TTL clear (default 5 min, configurable via `--locked-hwnd-ttl-min`). Manually verify: Alt+Tab → USER_ALT_TAB, click-elsewhere → USER_CLICK, no-input-then-flash → STEAL, Win+L → SESSION_LOCK, display-resolution change → USER_OTHER (with note), 6-minute idle then steal → STEAL with `locked_hwnd_before = 0x0` and note `"locked anchor expired"`. **Unit test** the truth table in TUnit — this is the most important unit-tested component.
9. **Channel pipeline.** Move enrichment + classification out of hook callbacks into a background `Task` draining a `Channel<RawEvent>` (bounded, capacity 1024, single-writer/single-reader). Hook callbacks now only timestamp + synchronous-snapshot + enqueue. Verify hook latency stays under 1 ms using `Stopwatch.GetTimestamp()`. On channel-full: increment a counter, attach `note="channel full"` to the dropped record's index, do NOT block in the hook.
10. **Parent chain walker.** Implement `NtQueryInformationProcess` (classes 0 + 60), `QueryFullProcessImageNameW`, `GetApplicationUserModelId`, `IsWow64Process2`, and `ReadProcessMemory` via `[LibraryImport]`. Walk parents for the focused PID with safety caps (PID==0/4, cycle, depth — **no `explorer.exe` stop**). For each node capture: full image path, basename, command line, `cwd` (always, via PEB+ReadProcessMemory), `package_aumi` for UWP processes (when cmdline empty or image in `SystemApps`/`WindowsApps`), and `env` (only when `--capture-env`). Handle WOW64 (32-bit-on-64-bit-OS) PEB layout differences. Verify on known cases: cmd.exe launched from Run dialog (parent = explorer.exe), from VS Code terminal (parent = Code.exe), from PowerShell (parent = pwsh.exe), Notepad UWP (populated `package_aumi`, empty cmdline). Verify the chain doesn't stop at explorer.exe — it walks through to userinit/wininit when those are reached.
11. **Exporters (multi-format).** Define a single canonical `EventRecord` value type (per-event, immutable). Implement JSONL first — it is the lossless encoding. Then CSV, logfmt, Markdown, plain text. Each is a separate writer class implementing a common `IEventExporter` interface; each encodes from the same `EventRecord`. Verify with format-appropriate viewers (Excel for CSV, `jq` for JSONL, `grep`/`awk` for logfmt, any Markdown viewer). For JSONL specifically: confirm `parent_chain` is a structured array, full image paths and `cwd` present per node, and (when `--capture-env`) populated `env`.
12. **HTML report on shutdown.** On graceful shutdown, read JSONL back (or use the in-memory accumulator) and render a single-file HTML page with embedded CSS/JS, sortable table, classification filter. Write to `spawnspotter-YYYY-MM-DD.html`.
13. **Console UX + lifecycle.** Implement Spectre.Console `Live` status line for `interactive`/`status-only` modes. Per-event console lines for verbosity ≥ 1. Implement `--duration` countdown via a `Task.Delay(duration)` linked to the shutdown cancellation token. Implement the exit-summary line printed to stdout on every shutdown path (Ctrl+C, duration expiration). Verify exit codes match §5.8 (0 / 1 / 2 / other-nonzero) on each path.
14. **Soak test (1 hour).** Run with VS Code + Claude Code (Playwright MCP) + Dev Containers extension active. Confirm STEAL rows show up with expected ancestry. Confirm typing in any other window does NOT produce STEAL rows. Confirm Alt+Tab does NOT produce STEAL rows.
15. **Polish.** Finalize README with build + run + flag reference (the latter can simply embed `--help` output). Verify the AOT-published binary runs on a clean Windows 11 machine without .NET runtime installed.
16. **24-hour run.** Real-world test on the user's machine. Confirm log size sane (<15 MB/day expected), no crashes, all exporters writing correctly.

---

## 7. Reference source code to study

For each component, the following existing implementations are the best reading material. **Do not copy-paste licensed code without honoring the license**; treat these as reference for technique.

| Component | Repo / URL | What to read | Why |
|---|---|---|---|
| `SetWinEventHook` in C# | [fjl/4080259 gist](https://gist.github.com/fjl/4080259) | All ~60 lines | Smallest correct C# example. P/Invoke signatures are reusable verbatim. |
| `SetWinEventHook` production pattern | [JocysCom/FocusLogger](https://github.com/JocysCom/FocusLogger) | The engine class and the native methods wrapper | Closest production-quality C# example. Active maintenance, .NET 8 WPF. Note: it polls instead of using WinEvent hooks for the *foreground* tracking, but uses related APIs cleanly. |
| Which WinEvents fire when (mental model) | [blep/win32_window_monitor](https://github.com/blep/win32_window_monitor) | `monitor.py` | Python, but readable. Shows EVENT_SYSTEM_FOREGROUND vs EVENT_OBJECT_FOCUS vs EVENT_OBJECT_SHOW vs EVENT_OBJECT_NAMECHANGE behaviors. Important for understanding why we use ONLY foreground. |
| Multi-event WinEventHook reference | [keturn/6695625 gist](https://gist.github.com/keturn/6695625) | `trackwindow.py` | Python, hooks 6 different WinEvent types. Useful if v2 needs more event types. |
| Old New Thing canonical sample | [devblogs.microsoft.com/oldnewthing/20131202](https://devblogs.microsoft.com/oldnewthing/20131202-00) | The full post | Raymond Chen's reference EVENT_SYSTEM_FOREGROUND implementation. C++, but authoritative. |
| `WH_KEYBOARD_LL` + `WH_MOUSE_LL` in C# (NuGet library) | [gmamaladze/globalmousekeyhook](https://github.com/gmamaladze/globalmousekeyhook) | `KeyboardWatcher.cs`, `MouseWatcher.cs`, hook installation | MIT-licensed mature C# library. If using as a dependency, skim to understand the abstraction. If implementing raw, this is the canonical reference. NuGet package: `MouseKeyHook`. |
| Raw P/Invoke for low-level hooks (no library) | gmamaladze/globalmousekeyhook | Source in the `Implementation/Hooks/` folder | Same repo; the internal P/Invoke layer is what you'd write by hand. |
| `NtQueryInformationProcess` signatures | [processhacker/phnt](https://github.com/processhacker/phnt) | `ntpsapi.h`, `ntpebteb.h` | Authoritative NT API headers. Defines `PROCESS_BASIC_INFORMATION` and the info-class enum, including class 60 `ProcessCommandLineInformation`. |
| `NtQueryInformationProcess` C# example | [SystemInformer's NativeApi folder](https://github.com/winsiderss/systeminformer) | Look for managed P/Invoke definitions in their .NET tooling, or search GitHub for `ProcessCommandLineInformation` + C# | Confirms the info class number (60) and the structure (UNICODE_STRING). |
| Keyboard-window correlation concept | [Selfspy](https://github.com/selfspy/selfspy) | `selfspy/sniff_win.py` and the SQLite schema | Python and abandoned, but conceptually closest: keystrokes tied to windows. The data model is similar to ours (events keyed by window+process+time). Don't replicate the encryption stuff. |
| Channel<T>-based producer/consumer pipelines | Microsoft docs: [System.Threading.Channels](https://learn.microsoft.com/dotnet/core/extensions/channels) | All of it | Standard .NET pattern for the hook-callback → consumer pipeline. |
| CSV column / export inspiration | JocysCom/FocusLogger | The CSV export feature | Their columns: `Date, PID, Process Name, Active, Mouse, Keyboard, Caret, Window Title, Window Class, Path`. Ours differs but the format conventions (UTC ISO 8601, CSV-escaped titles) are good to mirror. |
| What NOT to do: polling-only loggers | [fpsheaven/FocusGrabber](https://github.com/fpsheaven/FocusGrabber) | `focus.cpp` | 100 ms polling of `GetForegroundWindow`. Demonstrates why polling fails for sub-second flashes — it's the user's old 2014 binary that they had on disk. Useful as a counter-example. |
| Privacy hygiene for keyboard hooks | (no single source) | — | Industry convention: do not log key contents. Many AV vendors flag keyloggers; recording only categories + timestamps is the polite way. |
| Spectre.Console / Spectre.Console.Cli | [spectreconsole.net](https://spectreconsole.net) | The `Cli` docs and `Live` docs | Modern .NET CLI library; AOT-compatible. Used for both flag parsing / help output and the live status line. |
| TUnit | [github.com/thomhurst/TUnit](https://github.com/thomhurst/TUnit) | README + getting started | Modern source-generator-based test framework for .NET. Faster cold start than xUnit; AOT-aligned. |
| .NET Native AOT | [learn.microsoft.com/dotnet/core/deploying/native-aot](https://learn.microsoft.com/dotnet/core/deploying/native-aot) | Limitations + best practices | The constraints we are accepting day-1: LibraryImport, no dynamic loading, trimming. |
| `[LibraryImport]` source-gen P/Invoke | [learn.microsoft.com/dotnet/standard/native-interop/pinvoke-source-generation](https://learn.microsoft.com/dotnet/standard/native-interop/pinvoke-source-generation) | All of it | Replaces `[DllImport]`. Required for AOT. |
| `[UnmanagedCallersOnly]` for hook delegates | [learn.microsoft.com/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute](https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute) | Remarks section | How to expose static C# methods as native-callable function pointers under AOT. Replaces `Marshal.GetFunctionPointerForDelegate` for AOT scenarios. |

---

## 8. Risks & mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| **Short-lived flash exits before parent walker runs** — by the time the consumer task opens the PID, the process is gone, and `OpenProcess` returns null. | Medium | Lose image + cmdline for the focused process and its immediate parent — the very shortest, most interesting flashes. | Synchronously snapshot the focused process AND immediate parent's PID, full image path, AND full command line inside the foreground callback (see §5.2). Total cost ~100-200 µs (4 NT API calls), well within the 1 ms callback budget. Defer only the grandparent-and-up recursion to the consumer. If a deeper ancestor has exited by walker time, log "parent already exited" in the chain field for that node. |
| **Hook timeout unloads the hook** — Windows silently removes a low-level hook if its callback takes too long; default timeout is registry-controlled (`HKCU\Control Panel\Desktop\LowLevelHooksTimeout`, default 300 ms on modern Windows; was unlimited pre-Win7). | Medium | Tool silently stops working after some time. | Keep hook callbacks under 1 ms. Profile with `Stopwatch.GetTimestamp()` deltas during testing. Defer all I/O, all process queries, all allocation beyond a single struct into the channel. |
| **GC collects the hook delegate** — managed delegate passed to `SetWindowsHookEx` is held by native code; if the only managed reference is a local, GC will free it and the next callback AVs. | **N/A under AOT path** | — | Structurally impossible: hook callbacks are `static [UnmanagedCallersOnly]` methods compiled to direct native entry points (see §3, §5.1). No managed delegate exists to be collected. This row stays in the risk table to flag the trap for anyone who deviates from the AOT path. |
| **Antivirus / EDR flags the binary as a keylogger** — `SetWindowsHookEx(WH_KEYBOARD_LL, ...)` is a known telemetry signal for many EDR products. | Medium | False-positive quarantine. | (1) Document the design in the README and prominently note that key contents are not logged. (2) Sign the binary if a code-signing certificate is available. (3) If quarantined, add an AV exclusion for the binary path. (4) Consider whether the user's organization's EDR will allow this on their machine before extensive work. |
| **EVENT_SYSTEM_FOREGROUND fires multiple times per logical change** — some apps generate spurious foreground events on startup/activation. | High | Duplicate log rows. | Dedupe in the consumer: if `(hwnd, pid)` is identical to the previous event within 50 ms, drop. |
| **EVENT_SYSTEM_FOREGROUND does NOT fire for some focus changes** — UWP apps, modal dialogs, and certain shell behaviors may not generate it. | Low–Medium | Some steals missed. | Resolved in v1.0 per decision #27 (2026-05-24 refinement): `EVENT_OBJECT_FOCUS` and `EVENT_OBJECT_SHOW` are installed alongside `EVENT_SYSTEM_FOREGROUND` and deduped by HWND in the consumer (§5.2). |
| **Classifier false positives** — a slow-to-respond click can take >500 ms to result in foreground change. | Low | A few legitimate USER_CLICK events misclassified as STEAL. | Make threshold configurable. Default 500 ms is conservative; widen if needed. |
| **Tool runs from `Downloads` and gets cleaned up.** | Low | Logs lost. | Default log directory is `%LOCALAPPDATA%\SpawnSpotter\logs\`, not `Downloads`. Document this in README. |
| **Lock screen / unlock generate spurious rows** — when the PC locks (Win+L or auto-lock), foreground changes to `LogonUI.exe` (SYSTEM-owned, access-denied to standard user); unlock generates another foreground change classified by password-typing timing. Parent walker fails on LogonUI. | Certain (every lock/unlock cycle) | Rows pointing at `LogonUI.exe` / `LockApp` window class. | Resolved in v1.0 per decision #24 (2026-05-24 refinement): these events are classified as `SESSION_LOCK` (§5.5 step 1), counted separately from STEAL, and clearly distinguishable in the log. Parent-walker failure on LogonUI is acceptable — the `image_path` field still identifies it. `--ignore-image <glob>` is available for users who want to drop them entirely (decision #35). |
| **24h log file size** — if STEAL events are extremely frequent, the multi-format files could grow. | Low | Disk use. | Estimate from suspect cadence: Playwright MCP restarts ~tens of times/day, Dev Containers polls every few seconds = thousands per day. Each row ~500 bytes per format × 5–6 formats = ~3 MB/day per format, ~15 MB/day total worst case. Acceptable. No rotation logic added per §5.7. |
| **Debug build hides AOT regressions** — code that builds and runs under `dotnet run` may fail to AOT-publish (most often due to a non-`[LibraryImport]` P/Invoke slipping in, or reflection in a transitive dependency). | Medium | "Works on my machine" but the shipped artifact is broken. | Run `dotnet publish -c Release -r win-x64` as part of every commit's verification, not just at release time. Treat AOT publish failure as a build failure. |
| **Spectre.Console.Cli AOT-incompatibility** — though officially AOT-compatible in recent versions, edge cases (custom converters, exotic command-argument shapes) may emit AOT warnings or fail at runtime. | Low–Medium | Help text broken or app crashes on first command dispatch. | Pin Spectre.Console.Cli to a known-good version. Verify the entire `--help` and command-dispatch surface works under AOT publish during step 2 of the implementation order, before adding hook code. |
| **`[UnmanagedCallersOnly]` is static-only** — incompatible with the natural "instance per hook" object-oriented design. | Low | Slightly less elegant code structure than under JIT. | Plan accordingly: each hook callback is a static method that reads from a static (or `AsyncLocal`) "current hook installer" reference. Document the pattern in the hook installer's class header. |
| **Reference samples use `[DllImport]`** — most of the C#/C++ examples linked in §7 predate `[LibraryImport]`. | Certain | Mechanical translation needed when adopting any sample. | Translate signatures manually: `[DllImport("user32.dll")] static extern …` becomes `[LibraryImport("user32.dll")] static partial …` with the method declared `partial`. Update marshaling (especially strings) where needed. |
| **`EVENT_OBJECT_SHOW` event volume** — every tooltip, every menu, every transient popup raises `EVENT_OBJECT_SHOW` for some HWND. Even filtered to top-level visible windows, this can fire many times per second during normal use. | High | Hook-callback overhead; channel saturation; noisy logs at v≥3. | Filter aggressively in-callback (§5.2): require `idObject == OBJID_WINDOW`, `idChild == CHILDID_SELF`, `WS_VISIBLE` set, `WS_CHILD` clear, `GetWindow(hwnd, GW_OWNER) == NULL` (skip owned popups). Apply cross-source dedupe — a `SHOW` followed within 50 ms by a `FOREGROUND` for the same HWND is dropped. Cap channel at 1024 events; backpressure-drop with `note="channel full"` exposed in the exit summary. |
| **`ReadProcessMemory` failures during PEB walk** — target process may be 32-bit (different PEB layout), paged out (RAM pressure), in the process of exiting, or PPL-protected. | Medium | `cwd` and `env` fields show `<unavailable>` for some chain nodes. | Detect 32-bit-on-64-bit-OS via `IsWow64Process2` and use the WOW64 PEB layout (`PEB32` / `RTL_USER_PROCESS_PARAMETERS32` with 32-bit pointers and `ULONG` lengths). Wrap each `ReadProcessMemory` in a single 10 ms-backoff retry to absorb transient paging. On final failure, populate `cwd = "<unavailable>"` with `note` describing the failure mode; do not abort the rest of the chain walk. |
| **Environment-block secrets** — `--capture-env` reads the full env block which on developer machines routinely contains `GITHUB_TOKEN`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `AWS_SECRET_ACCESS_KEY`, `NPM_TOKEN`, DB connection strings, etc. | Certain when flag is on | Plaintext secrets in `*.jsonl` log files. | Default `--capture-env` to OFF. The flag's help text and the run banner both warn that JSONL files become secret-bearing artifacts under this flag. Documented in the README. Analyst is responsible for handling the resulting files. |
| **MouseKeyHook + SharpHook evaluated and rejected** — `MouseKeyHook` 5.7.1 (2023-04-10) is unmaintained and AOT-incompatible (`[DllImport]` + `Marshal.GetFunctionPointerForDelegate` + transitive `System.Windows.Forms`). `SharpHook` is AOT-compatible but bundles `libuiohook` as a native dependency. | Resolved 2026-05-24 | — | Hand-roll `[LibraryImport]` + `[UnmanagedCallersOnly]` keyboard/mouse hook P/Invoke (~80 lines). Privacy surface auditable (key categories only, no `vkCode` materialization). Recorded as decision #28. |
| **Hidden top-level window leaks across runs** — `RegisterClassExW` registers a window class by name in the process's atom table; running multiple instances or recycling an unclean-exit'd process could collide. | Low | First run after a crash may fail `RegisterClassExW` with `ERROR_CLASS_ALREADY_EXISTS`. | Pick a unique class name (e.g. `SpawnSpotter.MonitorWindow.{Process.GetCurrentProcess().Id}`) so collisions across instances are impossible; `ERROR_CLASS_ALREADY_EXISTS` from a clean run is then a real bug, not an environmental issue. Pair `UnregisterClassW` with `DestroyWindow` in the teardown path. |

---

## 9. Testing approach

1. **Unit-level isolation:** the classifier is pure (inputs: 3 timestamps + new HWND + previous locked HWND; output: classification + new locked HWND). Write a small xUnit test project covering the truth table.
2. **Hook smoke test:** a manual checklist in the README — "Alt+Tab between two windows; expect zero STEAL rows. Click on another window; expect zero STEAL rows. Wait 30 seconds without input while a known flash is occurring; expect STEAL rows."
3. **Parent-chain test:** launch `cmd.exe` from Run dialog, from VS Code's integrated terminal, and from a PowerShell window. Alt+Tab to the resulting cmd window in each case and verify the chain logged in the debug log matches expectations.
4. **Soak test:** 1 hour of normal use with all suspects active. Inspect the CSV. Expected: a handful of STEAL rows pointing to either Code.exe → cmd.exe (Dev Containers) or claude.exe → cmd.exe → npx → node (Playwright MCP).
5. **Stress check:** rapid alt-tabbing for 30 seconds to make sure no STEAL rows appear (this is the classifier's worst false-positive case).
6. **Privacy check:** grep the log for any letter sequences resembling typed text. There should be none — window titles are the only place text appears, and that's expected.

---

## 10. Decisions log

All planning-time questions resolved. The implementer follows these; deviations require explicit user re-approval.

| # | Decision | Choice | Why |
|---|---|---|---|
| 1 | Keyboard/mouse hook library | Raw `[LibraryImport]` P/Invoke (~80 lines) | Minimal deps; privacy surface easier to audit |
| 2 | Message loop | Hand-rolled `GetMessage`/`DispatchMessage` | Keeps TFM at `net10.0`; avoids `Microsoft.WindowsDesktop.App` |
| 3 | CLI parsing + help output | `Spectre.Console.Cli` | Best help formatting for humans/agents, AOT-compatible |
| 4 | Console status output | `Spectre.Console` Live API | Rich in-place redraw, transitive from Spectre.Console.Cli |
| 5 | Export formats (v1) | CSV + JSONL + logfmt + Markdown + plain text + HTML-on-shutdown | Covers spreadsheets, agents, grep pipes, human skim, sharing |
| 6 | File rotation | Daily only, no size cap | Expected volume tiny; YAGNI |
| 7 | Default console mode | `interactive` (visible status line + scrolling events) | Primary use is CLI; agents use `--mode silent` |
| 8 | Project layout | Single project at repo root; tests at `/tests/SpawnSpotter.Tests/` | Smallest layout that accommodates tests |
| 9 | Test framework | TUnit | Source-generator-based, AOT-aligned, modern |
| 10 | Publish profile | Native AOT from day 1 (`PublishAot=true`, `InvariantGlobalization=true`, `IlcOptimizationPreference=Size`) | Single-file, no runtime install, smallest binary |
| 11 | Hook install failure behavior | Exit non-zero immediately | Degraded mode produces garbage classifier output |
| 12 | Threshold flag design | Single `--threshold-ms` default + advanced per-source override flags | Simple default; power flags for tuning |
| 13 | No-args behavior | Print help, exit 0 | Self-documenting; no out-of-band docs |
| 14 | Subcommand structure | `watch` to run, `version` for version | Standard CLI shape; future-proof for additional commands |
| 15 | Code signing | Unsigned for v1; **do not modify AV configuration** | Personal use; tool runs on user's own machine |
| 16 | C# language version | C# 14 (default for `net10.0`) with modern features (collection expressions, primary constructors, the `field` keyword, params collections, extension members) | Modern, clean, minimal |
| 17 | Privacy of key events | Key categories only — never vkCode, scan code, or text contents | Both ethical and AV/EDR-friendly |
| 18 | `LockedHwnd` semantics | Keep as designed — updated only on USER_* events (click / alt-tab / other-system); initialized at startup from `GetForegroundWindow()` | Fits the click-then-idle usage pattern; "TypingHwnd" alternative considered and rejected because it would be stale or null when the user clicks and then doesn't type |
| 19 | `idle_time_ms` schema field | Add as derived convenience: `min(key_age_ms, mouse_age_ms)` | Redundant with key_age + mouse_age but saves the analyst a per-row mental calculation |
| 20 | Synchronous parent snapshot | Inside the foreground hook callback, snapshot PID + image path + full command line for BOTH the focused process and its immediate parent | Survives short-lived flash exit before the consumer task wakes up; ~100-200 µs cost vs 1 ms callback budget |
| 21 | `--duration <span>` flag | Optional; human-friendly format (`45m`, `2h`, `2h30m`, etc.); custom Spectre `TypeConverter`; default unset = run forever | Lets agents bound their tool calls without needing to kill the process; clean exit on expiration |
| 22 | Exit summary | Single-line summary printed to stdout in ALL modes (including `silent`) on graceful shutdown | Agents capturing stdout get an instant verdict without parsing log files |
| 23 | Exit codes | `0` = clean shutdown (Ctrl+C or duration); `1` = startup error (e.g. hook install failure); `2` = bad args; other non-zero = unhandled exception | Agents branch on exit code instead of parsing stderr |
| 24 | Lock screen / unlock noise | New `SESSION_LOCK` classification for `LogonUI.exe` + `LockApp` window-class events; emitted at verbosity 0 alongside STEAL but counted in a separate bucket; **pulled forward into v1.0** | Q13 (2026-05-24). Cheap to detect (image-name + window-class match), keeps STEAL bucket clean, analyst can filter trivially |
| 25 | `LockedHwnd` robustness | `IsWindow` validation on every event + idle TTL (default 5 min, configurable via `--locked-hwnd-ttl-min`; `0` disables TTL) | Q12 — "take the effort to keep working forever without problems". Validate-then-expire eliminates stale-anchor surprises across multi-hour runs |
| 26 | Canonical record schema | Single in-memory `EventRecord` value type; all six exporters encode from it; JSONL is the lossless representation; line-oriented formats render a basename-only chain | Q5+Q6. Avoids per-exporter schema drift; JSONL becomes the source of truth and the input for HTML rendering on shutdown |
| 27 | Additional WinEvent sources | Install `EVENT_OBJECT_SHOW` and `EVENT_OBJECT_FOCUS` from v1.0 in addition to `EVENT_SYSTEM_FOREGROUND`; tag each row with `monitored_via`; cross-source dedupe by HWND in a 50 ms window | Q18 (pull-forward). Catches flashes that show without taking foreground (the user's primary symptom) and UWP focus changes that bypass `EVENT_SYSTEM_FOREGROUND` |
| 28 | Hook library | Hand-roll `[LibraryImport]` + `[UnmanagedCallersOnly]` keyboard/mouse hook P/Invoke (~80 lines). `MouseKeyHook` rejected (unmaintained, AOT-incompatible, WinForms transitive dep). `SharpHook` rejected (libuiohook native dep contradicts slim-binary goal) | Q8 — audited 2026-05-24. AOT-clean by construction, no third-party native code, fully auditable privacy surface |
| 29 | Display / DPI suppression | Hidden top-level window subscribes to `WM_DISPLAYCHANGE` and `WM_DPICHANGED`; for 5 seconds after either, foreground-change events are classified as `USER_OTHER` with note `"monitor topology change"` (not STEAL) | Q14. Docking/undocking and resolution changes cycle focus through several windows in <100 ms; these are user-initiated even if no recent click/keystroke |
| 30 | UWP / Store app cmdline | When command line is empty (or image is in `SystemApps`/`WindowsApps`), query `GetApplicationUserModelId` and populate `package_aumi` on the chain node | Q15. UWP processes don't have meaningful classical cmdlines; AUMI is the equivalent identifier |
| 31 | Parent-walker stop conditions | Stop at PID==0, PID==4, cycle, depth > `--max-chain-depth`. **No `explorer.exe` stop heuristic** | Q16. Shell may be replaced; explorer is occasionally a useful intermediate parent; the remaining stops are sufficient and never wrong |
| 32 | Process `cwd` always captured | Read PEB → `RTL_USER_PROCESS_PARAMETERS.CurrentDirectory` via `ReadProcessMemory` for every chain node; surface in JSONL | Q23. Primary discriminator between 5 invocations of the same `npx -y @playwright/mcp` — which cwd launched which |
| 33 | Full image paths in JSONL | Always present per chain node in JSONL output; line-oriented formats stay basename-only for readability | Pulled forward from v1.5 deferral per Q18. HTML report surfaces full paths via expandable rows |
| 34 | Process environment opt-in | `--capture-env` flag, default OFF; when ON, full env blocks are read via `ReadProcessMemory` and stored in JSONL chain nodes | Q23. Useful for diagnostic deep-dives but routinely contains secrets (`*_TOKEN`, `*_KEY`); off-by-default is the safe choice; help-text + run banner warn explicitly |
| 35 | `--ignore-class` / `--ignore-image` filters | Glob-pattern flags pulled forward from v1.1 deferral; applied in classifier after SESSION_LOCK / monitor suppression but before standard classification | Q18 pull-forward. Lets the analyst suppress known-noise sources at the source rather than post-hoc filtering log files |
| 36 | `--max-steals N` early termination | New flag; stops after N STEAL events have been logged; same shutdown path as `--duration` expiration; combines with `--duration` (whichever triggers first) | Q28. Agents can capture one representative STEAL and exit without timing-based guesswork |
| 37 | Open-source release | v1.0 is internal use; v1.1 will be the first public release | Q31. Defers signing, contributor docs, telemetry-policy, and AV-vendor outreach decisions to v1.1 |
| 38 | Self-detection | Do NOT suppress foreground events whose focused PID is SpawnSpotter itself | Q17. Verifying the tool's own focus events demonstrates it's functioning end-to-end |

---

## Appendix A — Full inventory of tools evaluated

This section is preserved verbatim from the research that led to this plan. Tools are grouped by tier (fitness to the target use case).

### Tier 1 — Event-driven (catches sub-second changes), no admin required

| Tool | URL | Lang | Detection | Parent PID? | Output | License | Last update |
|---|---|---|---|---|---|---|---|
| win32_window_monitor (blep) | https://github.com/blep/win32_window_monitor | Python | `SetWinEventHook` (foreground/focus/show/capture) | No | stdout TSV | MIT | 2023-11 |
| keturn gist `trackwindow.py` | https://gist.github.com/keturn/6695625 | Python | `SetWinEventHook`, 6 event types incl. show, dialog, unminimize | No (PID+exe) | stdout TSV | unspecified | 2013-09 |
| bwright86 PS gist | https://gist.github.com/bwright86/c9a8933c21dd9fb04b5c1c577d050c5b | PowerShell | `SetWinEventHook` EVENT_SYSTEM_FOREGROUND | No | Write-Host (pipe to file) | unspecified | 2022-10 |
| Old New Thing sample (Raymond Chen) | https://devblogs.microsoft.com/oldnewthing/20131202-00 | C++ | `SetWinEventHook` reference impl | No | stdout | MS sample | 2013-12 |
| fjl C# gist | https://gist.github.com/fjl/4080259 | C# | `SetWinEventHook` | No | console | unspecified | 2012-11 |
| Danesprite/windows-fun listener | https://github.com/Danesprite/windows-fun | Python | `SetWinEventHook` | No | stdout | none/dormant | 2017 |

### Tier 2 — Fast polling, no admin (may miss the very shortest flashes)

| Tool | URL | Lang | Poll interval | Parent PID? | Output | License |
|---|---|---|---|---|---|---|
| JocysCom/FocusLogger | https://github.com/JocysCom/FocusLogger | C# WPF .NET 8 | 1 ms timer (`GetForegroundWindow` + `GetGUIThreadInfo`) | No | GUI grid + CSV export | GPL-3.0 |
| simeneilevstjonn/FocusLogger | https://github.com/simeneilevstjonn/FocusLogger | C# | tight loop, no sleep | No | stdout, title only | MIT |
| fpsheaven/FocusGrabber (happydroid's `focus.exe`) | https://github.com/fpsheaven/FocusGrabber | C++ | `Sleep(100)` | No | stdout | public domain (in source) |
| gsuuon gist | https://gist.github.com/gsuuon/dc24398339d1196a1e9d50d293727911 | F# | 100 ms process diff | No (but exe path) | stdout | unspecified |
| AdminScope Window Focus Logger (CLI) | http://www.adminscope.com/downloads/window-focus-logger/ | closed | unknown | No | text logfile | freeware |
| AdminScope Window Focus Logger (GUI) | http://www.adminscope.com/downloads/window-focus-logger-gui/ | closed | unknown | No | text + tray | freeware |

### Tier 3 — Admin required (solves the whole problem in one shot, but blocked by user's standard-user constraint)

| Tool | URL | What it gives | Admin |
|---|---|---|---|
| Sysinternals Process Monitor | https://learn.microsoft.com/sysinternals/downloads/procmon | ETW kernel events, full process tree, native .pml | Yes (driver) |
| Sysmon Event ID 1 | https://learn.microsoft.com/sysinternals/downloads/sysmon | Persistent: `Image`, **`ParentImage`**, **`ParentCommandLine`**, GUID, hashes | Yes (install) |
| wtrace | https://github.com/lowleveldesign/wtrace | ETW user-friendly CLI, parent PID | Yes |
| Win Security 4688 (Audit Process Creation + cmdline GPO) | (built-in audit policy) | Native event log, parent PID + cmdline | Yes (GPO) |

### Tier 4 — Productivity time-trackers (≥1 s polling — unsuitable for sub-second flashes, listed because they kept appearing in searches)

| Tool | URL | Lang | License | Notes |
|---|---|---|---|---|
| ActivityWatch (aw-watcher-window) | https://github.com/ActivityWatch/aw-watcher-window | Python | MPL-2.0 | Active. 1 s heartbeats. |
| ActivityWatch (aw-watcher-input) | (sibling watcher) | Rust + Python | MPL-2.0 | Useful concept — separate input watcher correlated to window watcher via timestamps. |
| Tockler | https://github.com/MayGo/tockler | TypeScript/Electron | GPL-2.0 | ~1 s poll. |
| TheCodeArtist/Active-Window-Logger | https://github.com/TheCodeArtist/Active-Window-Logger | VB.NET | CC BY-SA 4.0 | Abandoned 2016. |
| Selfspy | https://github.com/selfspy/selfspy | Python | GPL-3.0 | Dormant since ~2015. Closest in concept (keystrokes ↔ windows). |
| RomanKornev/Focus | https://github.com/RomanKornev/Focus | Python (Jupyter) | MIT | 2019. |
| Screeny | https://github.com/ArnoGevorkyan/Screeny | C# WinUI3 | Apache-2.0 | Active. |
| owlwindowlogger | https://github.com/seanbuscay/owlwindowlogger | Python | unspecified | Abandoned 2012. |
| arbtt | https://arbtt.nomeata.de | Haskell | GPL-2.0 | Active. |
| sparksbat C# gist | https://gist.github.com/sparksbat/38d3a8c31f36d18cc497831631691067 | C# | unspecified | 5 s loop. |
| active-win-log (npm) | https://www.npmjs.com/package/active-win-log | Node | MIT | Dormant. |

### Other references encountered

| Resource | URL | Why noted |
|---|---|---|
| Claude Code issue: console window flashing on Windows | https://github.com/anthropics/claude-code/issues/14828 | The user's specific Playwright MCP flash problem upstream. |
| Claude Code issue: related Windows console flash | https://github.com/anthropics/claude-code/issues/21375 | Same root cause family. |
| System Informer | https://github.com/winsiderss/systeminformer | Process Hacker fork. Useful NT API P/Invoke reference. |
| System Informer issue #2813 — misses short-lived processes | https://github.com/winsiderss/systeminformer/issues/2813 | Maintainer-confirmed that polling-based process viewers miss short-lived processes. |
| ProcessSpawnControl | https://github.com/felixweyne/ProcessSpawnControl | WMI-based, no admin, but GUI-popup UX. |
| malcomvetter/WMIProcessWatcher | https://github.com/malcomvetter/WMIProcessWatcher | Author notes: WMI misses short-lived process command lines. |
| Use PowerShell to Monitor for Process Startup | https://devblogs.microsoft.com/scripting/use-powershell-to-monitor-for-process-startup/ | Confirms `Win32_ProcessStartTrace` needs elevated privileges. |
| AutoHotkey forum: `SetWinEventHook` library | https://www.autohotkey.com/board/topic/85231-lib-hook-winevents-to-catch-windows-creationdestruction/ | AHK community foundation script for the same goal. |
| AutoHotkey v2 ProcessGetParent docs | https://www.autohotkey.com/docs/v2/lib/ProcessGetParent.htm | AHK helper for parent PID lookup. |
| Winhelponline: Command Prompt Flashes and Closes Quickly | https://www.winhelponline.com/blog/find-unknown-program-open-and-close-immediately/ | User-facing guide for the same problem. |
| Microsoft Q&A: CMD Window Flashes Briefly | https://learn.microsoft.com/en-us/answers/questions/3853220/cmd-window-flashes-briefly-possibly-linked-to-svch | Recurring question on MS support. |
| Win32_ProcessStartTrace reference | https://learn.microsoft.com/en-us/previous-versions/windows/desktop/krnlprov/win32-processstarttrace | Documents privilege requirements. |
| processhacker/phnt | https://github.com/processhacker/phnt | NT API headers; authoritative for `NtQueryInformationProcess`. |
| `System.Threading.Channels` docs | https://learn.microsoft.com/dotnet/core/extensions/channels | The pipeline primitive we use. |

---

## Appendix B — Conversation context and decision history

This appendix summarizes the conversation that produced this plan so the implementing agent has full context without needing to re-derive it.

**Origin of the requirement.** The user reported on 2026-05-23 that "multiple short window flashes every hour" steal focus for under a second and corrupt typing. Prior Claude Code sessions had checked the running process list *after* events with inconclusive results (the offending process had already exited by the time `Get-CimInstance Win32_Process` ran). The user explicitly wanted persistent (24h+) monitoring that catches short-lived windows and traces them to a parent application.

**Two confirmed suspects (from prior investigation, 2026-05-21):**
- Claude Code's Playwright MCP servers: five `.mcp.json` files under `C:\Source\SixFive7\` all launching `npx -y @playwright/mcp@latest` via Claude Code's `cmd /d /s /c` wrapper *without* `windowsHide`. Each (re)start flashes `cmd.exe` + `conhost.exe`. Known upstream: anthropics/claude-code #14828, #21375 — unfixed.
- VS Code Dev Containers extension (`ms-vscode-remote.remote-containers` v0.459.0) polling Docker engine via short-lived `docker.exe`, activated `onStartupFinished` in every VS Code window even when no `devcontainer.json` is present.

**Ruled likely-not-visible:** Wispr Flow profiling worker spawning two `powershell.exe` every 180 s — both call sites pass `windowsHide:true`.

**Investigation of options proceeded in three rounds.**

*Round 1: enumerate approaches.* Identified 5 directions: `SetWinEventHook`-based DIY, WMI process-creation subscription, System Informer GUI, hybrid (window-hook + process-spawn log), and fast polling. Concluded that the hybrid approach without admin is the closest non-admin substitute for Sysmon, but it's the most code to wire up.

*Round 2: search for an existing turnkey tool.* A research agent searched broadly and concluded no purpose-built no-admin tool exists. Closest candidates: `wtrace` (admin-only), and an AutoHotkey-based composition. Recommended either getting one-time admin for wtrace, or composing the AHK script.

*Round 3: user introduced a mystery binary.* The user pointed to `C:\Users\jori\Downloads\Focuslogger.exe` they'd had for years. An initial research agent hallucinated provenance, claiming it was an internal VoIPfabric/NetPortal tool based on the user's email and an embedded PDB path `c:\DEV\Clients\NetPortal\trunk\Windows\focus\Release\focus.pdb`. The user corrected: the binary is from https://github.com/fpsheaven/FocusGrabber — happydroid's "focus" tool, mirrored by fpsheaven. The PDB path reflects happydroid's own freelance workspace at the time of compile (March 2014), unrelated to VoIPfabric. The reconstructed behavior (`Sleep(100)` + `GetForegroundWindow` polling + stdout) matched the GitHub source. The binary is unsuitable for the use case (polling, no parent chain).

The user also provided several tools the prior search had missed: JocysCom/FocusLogger, simeneilevstjonn/FocusLogger, AdminScope's two tools, TheCodeArtist's logger, the gsuuon and keturn gists. A subsequent exhaustive search expanded the inventory to ~25 tools (see Appendix A).

**Final user requirements (the trigger for this plan).** On 2026-05-23 the user clarified that the tool must:
1. Monitor the full parent process chain including subprocesses and command-line tools.
2. List focus changes that happened **without** mouse press or Alt+Tab.
3. Lock onto the window the user is typing into, watching the keyboard to discriminate Alt+Tab from text typing.
4. Be written in C# (user's preference) on .NET 10.

The user explicitly declined scaffolding and requested this `plan.md` for handoff to another agent.

**Key design decisions that influenced this plan:**
- Event-driven `SetWinEventHook` over polling — the only way to catch sub-second flashes reliably.
- Three hooks on one STA thread, work deferred via `Channel<T>` — required by the <300 ms hook-callback timeout.
- `NtQueryInformationProcess` (NT API) instead of WMI `Win32_Process` — WMI is too slow for short-lived processes, and `ProcessCommandLineInformation` (info class 60) gives command lines without admin.
- The "input-source filter" is THE differentiator from every existing tool. The classifier compares foreground-change timestamps to keyboard/mouse timestamps and labels STEAL only when no plausible user input preceded the change.
- Privacy: keystroke contents are NEVER stored or logged. Only categories + timestamps. This is both an ethics decision and an AV/EDR-friendliness decision.
- No admin, no driver, no service install — the user does not have admin on this machine.
- Append-only CSV — simplest crash-safe persistence; no SQLite dependency.
- C# .NET 10 — user-stated preference and latest stable SDK.

**Memory written during this investigation (for cross-reference if reopening the topic):**
- `flashing-windows-root-causes.md` — the two confirmed suspects and the no-admin constraint.
- `focuslogger-provenance.md` — corrected identification of `Focuslogger.exe` as happydroid's tool, with the hallucination warning attached so future sessions don't repeat the mistake.

**2026-05-24 refinement (version 1.3).** A follow-up Q&A pass produced this version. The implementer should be aware of:

- §3, §5.1: rewrote hook-installation guidance to reflect the AOT path. Callbacks are static `[UnmanagedCallersOnly]` methods passed as function pointers via `&Callback`; no managed delegate, no `GCHandle.Alloc` pinning. The prior advice about delegate pinning was a JIT-era hangover and contradicted the §3 AOT mandate.
- §3: audited `MouseKeyHook` (gmamaladze) and `SharpHook` (TolikPylypchuk) on 2026-05-24. `MouseKeyHook` rejected as unmaintained (last release 2023-04-10) and AOT-incompatible (`[DllImport]` + `Marshal.GetFunctionPointerForDelegate` + transitive `System.Windows.Forms`). `SharpHook` is AOT-compatible but bundles `libuiohook` as a native dependency, defeating the slim-binary goal. Hand-rolled `[LibraryImport]` confirmed as the chosen path.
- §4, §5.2: pulled `EVENT_OBJECT_SHOW` + `EVENT_OBJECT_FOCUS` forward from v1.1 to v1.0; added cross-source dedupe; added a hidden top-level window owned by the STA thread.
- §5.5: added `SESSION_LOCK` classification for `LogonUI.exe` + `LockApp` window class; added monitor topology suppression (`USER_OTHER` with note for 5 s after `WM_DISPLAYCHANGE` / `WM_DPICHANGED`); added `--ignore-class` / `--ignore-image` filters; added `LockedHwnd` robustness (`IsWindow` validation + 5 min idle TTL, both configurable).
- §5.6: removed `explorer.exe` stop heuristic; added always-on `cwd` capture via PEB+ReadProcessMemory; added opt-in `--capture-env` for full env blocks (with explicit secrets warning); added UWP `package_aumi` fallback for processes with empty cmdline.
- §5.7: canonicalized the schema around a single `EventRecord` value type; full image paths and `cwd` in JSONL; added `monitored_via` and `package_aumi` fields.
- §5.9: added flags `--max-steals`, `--ignore-class`, `--ignore-image`, `--locked-hwnd-ttl-min`, `--capture-env`.
- §6, §8: implementation steps and risks updated to match the above.
- v1.0 remains personal use; open-source release deferred to v1.1.
- Explicitly rejected in this pass and now listed in §2 OUT OF SCOPE for future agents: two-process / sentinel-and-consumer architecture (Q19); anthropic-bug-report subcommand (Q20); JSONL replay subcommand (Q21); local HTTP/SSE endpoint (Q22); toast notifications on STEAL (Q24); coalesced burst events (Q25); heartbeat rows (Q26); `--auto-restore` (Q27); polling fallback (Q29).

---

*End of plan. Document version: 1.3 — 2026-05-24 (AOT-clean callbacks; SHOW/FOCUS pulled forward; SESSION_LOCK; monitor suppression; LockedHwnd robustness; cwd + opt-in env; ignore filters; `--max-steals`; explorer-stop removed).*
