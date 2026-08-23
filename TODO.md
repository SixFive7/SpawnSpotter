# SpawnSpotter - TODO

Open repo-level tasks. Cross-cutting items only; per-feature work belongs in code
comments or commit messages.

Every claim below was verified against source at the line cited. Where a fix is
"preferred", the alternative is recorded so the reasoning is not lost.

## 1. Launch causality - one rule, two bugs

**Highest-value item: two independent investigations converged on the same mechanism.**

Compare the focused process's creation time against the last input and the event time:

- **Born after the last input, window appears within a launch window (~2-3 s)** - the
  user launched it. Attribute to the user.
- **Process is old** - the window was *raised*, not launched, so the parent chain is
  historical and must not be presented as causal.

`ChainNode.CreateTimeUtc` is already captured on every node (added with the PID-reuse
fix), so both operands are in hand at the sink with zero extra syscalls -
`EnrichmentPipeline` already reaches into `chain[0]` for `SessionId`.

### 1a. Raised vs launched: a chain is only causal when the process was just spawned

When an already-running window is merely raised, the parent chain answers "who
originally started this app" - a different question, and a misleading one to place next
to a `STEAL`. In the 2026-08-16 incident both windows were raised: their processes were
three days and one day old. The chain was *also* fabricated (PID reuse - fixed, see
CHANGELOG), but fixing that only removed the wrong answer. The corrected chain reads
`firefox.exe <- <parent exited, PID reused>`, which is honest and useless.

### 1b. FP-4: shell-launched apps cannot be explained by any threshold

`KeyCategorizer.cs:35-39` maps `Vk.RETURN or Vk.BACK` to `KeyCategory.Navigation`. The
classifier has exactly three input branches - AltTab, Click, OtherSystemKey
(`FocusClassifier.cs:99-107`). Navigation and TextLike keys feed no branch, so **Enter
can never explain a focus change at any threshold value**.

Observed 2026-08-23: Win key, Search, Enter, then `mmc.exe virtmgmt.msc` spawned by
explorer 141 ms after the Enter release, window 765 ms after process start, logged
`MAYBE_STEAL` because the Win key's `--threshold-other-ms` (1500 ms) had expired ~850 ms
earlier. Entirely user-driven.

Raising `OtherThresholdMs` past app-launch latency fixes this one instance and no launch
driven by a navigation or text key. The launch-causality rule is the general answer.

**Known constraints for 1a/1b.** Process age is a proxy, not the signal: a long-running
process opening a brand-new window (Outlook reminder, new browser window) is launch-like
with an old process. Multi-process apps report the broker's PID - Chromium and Firefox
return the browser process, and UWP frames belong to `ApplicationFrameHost.exe`; there is
no unwrap for either today. Creation time is null for PPL-protected targets and for ETW
rundown entries, so "unknown" must be a third branch (`ParentLinkVerifier.Check` is the
in-repo precedent).

## 2. FP-1: EVENT_OBJECT_SHOW is treated as focus acquisition, unverified

`FocusClassifier.Classify` never consults `input.MonitoredVia` - grep for `MonitoredVia`
across `src/Classifier` returns nothing. A window merely being *shown* takes the same
path as `EVENT_SYSTEM_FOREGROUND` and can be labelled STEAL / MAYBE_STEAL.

- 2026-08-23 18:34:51.595Z STEAL, `ConsoleWindowClass`, via `EVENT_OBJECT_SHOW`.
  Independent probe ground truth for the same HWND: `everFg=False` - it never held the
  foreground. Spawned `CREATE_NEW_CONSOLE + STARTF_USESHOWWINDOW/SW_SHOWMINNOACTIVE`.
- 18:55:01.045Z and 18:56:19.854Z MAYBE_STEAL, `GhostDivider`, explorer.exe, via
  `EVENT_OBJECT_SHOW`. `locked_hwnd_before` unchanged and no FOCUS_RESTORED followed -
  the real foreground never moved.

**Fix:** when `MonitoredVia == EventObjectShow`, require `GetForegroundWindow() == hwnd`
before any steal verdict; otherwise demote to benign/diagnostic.

**Secondary damage:** the first false positive parks the locked anchor on the shell
process, so following shows read as SAME_APP - and while the anchor sits on
explorer.exe, genuine explorer-spawned steals are masked.

## 3. FP-2: GhostDivider missing from the shell-transient catalogue

`ShellTransientPatterns.BuiltIn` (`:26-40`) holds six globs; `GhostDivider`
(explorer.exe-owned, untitled, transient) is not one, so it falls through to step 6.

Six GhostDivider events on 2026-08-23: USER_CLICK x2, SAME_APP x3, MAYBE_STEAL x2.
Identical artifact every time - the verdict is decided solely by `mouse_age` against
`ClickThresholdMs`.

**Fix:** add `GhostDivider` to `BuiltIn`. Workaround today: `--shell-class GhostDivider`.

## 4. FP-3: cold start forces STEAL instead of MAYBE_STEAL

`FocusClassifier.cs:141` - `idleMs = input.LastInputTickMs > 0 ? ... : long.MaxValue`.
Before the first observed input `idleMs == long.MaxValue`, which is
`>= StealActiveWindowMs`, so every unexplained focus change in the startup window becomes
high-confidence STEAL.

All four events in the first 47 s of the 2026-08-23 run carried `key_age=-1 mouse_age=-1
idle=-1` and were logged STEAL. One was independently confirmed to be no steal at all.

**Fix:** treat `LastInputTickMs == 0` as unknown (MAYBE_STEAL), or suppress the
confidence split until the first input is observed.

## 5. False negatives - do not regress these while fixing the above

- **FN-1.** `FocusClassifier.cs:126` gates `PrevWindowClosed` on
  `PrevForegroundHwnd != Zero && !PrevForegroundIsAlive` with **no test of the receiving
  window's age**, so it swallows a real steal. 2026-08-23 18:34:54.625Z,
  `ConsoleWindowClass`, ground truth `everFg=True`; the receiving window was created
  79 ms *into* the event, i.e. after the previous foreground died, so it cannot be the
  fallback recipient. Gate on the receiving window pre-existing that destruction.
- **FN-2.** `SameApp` (`:124`) compares PIDs. With Windows Terminal as the default
  console host (`DelegationConsole={2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}`), every
  console flash is a `WindowsTerminal.exe` window, so once any WT window holds foreground
  later console spawns compare equal and are masked. Observed 18:35:04.195Z. The same
  host-vs-launcher confusion makes the chain non-causal: it reads
  `WindowsTerminal.exe <- explorer.exe <- winlogon.exe` while the culprit is a sibling
  `cmd.exe` spawned milliseconds earlier and already sitting in the ETW spawn registry.

## 6. Naming the actor - what Windows will and will not tell us

**No documented API or event payload names the initiator.** WinEvents give the window
gaining foreground; every Win32k focus event payload is old-to-new. The same manifest
emits `CallerPid` for clipboard reads and full `SourcePID`/`TargetPID` for UIPI
*denials*, so the omission from the focus payloads looks deliberate.

**But the ETW envelope leaks it anyway - measured, not theorised.** See the confirmed
experiment below: `EVENT_HEADER.ProcessId`/`ThreadId` on the Win32k focus events name the
thread that *executed* the foreground change, which is the caller. This is the single
most important finding for attribution and it reverses the earlier conclusion that the
initiator was unreachable.

`AllowSetForegroundWindow` and `CoAllowSetForegroundWindow` let process A donate
foreground rights to B, with no event, audit, or query API for the grant. Any scheme is
legitimately defeatable and silently so - output must never imply certainty it lacks.

The goal is therefore not "name the culprit" but "stop pretending, and name plausible
suspects honestly". Two tractable follow-ons:

- **Actor correlation.** A second, user-mode ETW session on
  `Microsoft-Windows-TaskScheduler` (no elevation needed, stable schema, low volume):
  event 129 `Created Task Process` carries `TaskName`, `Path`, `ProcessID`. Correlating a
  foreground disturbance with task launches in the preceding ~2 s would have named
  `\Microsoft\Windows\Patch Claude Code Extension` in the 2026-08-16 incident instead of
  "some powershell". Highest-value attribution win available.
- **Win32k ETW session - CONFIRMED to name the caller. Promote to top of attribution
  work.** Experiment run 2026-08-23, two independent trials, Win11 26200. Setup: a plain
  Win32 target window T, and a separate caller process C whose own console holds the
  foreground, which then calls `SetForegroundWindow(T)`. Caller and gainer are different
  processes by construction.

  | Run | C (caller) pid/tid | payload old -> new | ETW header pid/tid |
  |---|---|---|---|
  | 1 | 89100 / 60904 | 69472 -> 13632 (T) | **89100 / 60904** |
  | 2 | 37540 / 52596 | 44064 -> 11072 (T) | **37540 / 52596** |

  In both runs the header names C, which is *neither* the process losing focus nor the
  one gaining it, and the header TID matches the thread id C recorded about itself
  exactly. Events 26 (`FocusedProcessChange`) and 2 (`FocusChange`) both carry it. When an
  app raises its own window the header equals the gainer, which is consistent: the
  initiator and the gainer are then the same process.

  Payloads are small and fixed-layout (event 26 is three `UInt32`s; event 2 is two), so
  hand-decoding as `EtwPayloadDecoder` already does is viable - no TDH needed. Guard on
  `EventDescriptor.Version` and refuse unknown versions rather than misparse: the focus
  event set has demonstrably grown across builds.

  **Limits, so this is not oversold.** Two trials on one build. Only
  `SetForegroundWindow` was exercised - `SwitchToThisWindow`, `ShowWindow`,
  `SetWindowPos` and shell-initiated activation are untested and may or may not stamp the
  same way. It names the *caller*, which is not always the instigator: the
  `AllowSetForegroundWindow` donation hole below still applies. Kernel-mode provider, so
  elevation is mandatory.

  Artifacts: `C:\Users\jori\Downloads\tmp-fgprobe` (`REPORT-run1.txt`, `REPORT.txt`,
  `probe.ps1`, `target.ps1`, `caller.ps1`).

Operational note if a Win32k session is ever added: **kernel-mode providers yield zero
events to an unelevated collector, silently** - the session starts, tooling reports
success, `EventsLost = 0`, and nothing arrives. Any such session needs an event-count
watchdog.

## Resolved

### Versioning: pin a real version number - DONE

Resolved in `c6441d0` by adopting **MinVer** (option 2 of the four originally
considered). `Directory.Packages.props` pins `MinVer` 6.0.0; `SpawnSpotter.csproj` sets
`<MinVerTagPrefix>v</MinVerTagPrefix>`; `Microsoft.SourceLink.GitHub` populates
`SourceRevisionId` so the SDK appends `+<sha>` to `InformationalVersion`.
`src/Cli/VersionInfo.cs` is the single reader and splits it into SemVer core,
pre-release suffix, and short SHA.

Version numbers are therefore tag-driven with no per-release edit. `v1.0.0` is currently
the only tag, so commits after it build as pre-release (`1.0.1-alpha.0.N`) until the next
tag is cut. The README's `version` claim is now accurate.
