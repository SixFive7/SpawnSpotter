# Changelog

All notable changes to SpawnSpotter are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and SpawnSpotter
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`MAJOR.MINOR.PATCH` bumps are driven by the user-visible CLI surface:

- **MAJOR** - breaking change to a CLI flag, exit code, log schema, or default behavior.
- **MINOR** - new feature, new flag, or new event classification.
- **PATCH** - bug fix or doc-only change with no observable surface change.

## [Unreleased]

### Fixed

- **Parent chain no longer fabricates ancestors through PID reuse.** A Windows PID
  identifies a slot, not a process: the kernel stamps a creator PID at creation and
  never updates it, so once the creator exits the number is recycled and the chain
  walker's "who holds this PID now" lookup starts answering with an unrelated
  process - grafting that stranger's whole ancestry onto the focused window. The
  walker now records each process's creation time and enforces the invariant that a
  parent cannot be created after its child. A proven violation truncates the chain
  with a terminal node marked `chain truncated: pid reused (candidate created after
  child)`; an unprovable link (creation time unknown on either side) is kept but
  annotated `parent link unverified (creation time unknown)` rather than dropped.
  The check covers the immediate parent as well as walked ancestors, and applies to
  both the live `OpenProcess` path and the ETW-registry fallback.

  The misattribution was biased, not random: the most likely current occupant of any
  recycled PID is whichever process churns through PIDs fastest, so the detector
  systematically accused the busiest process-spawner on the machine.

### Added

- Per-chain-node process creation time, captured via `GetProcessTimes` on the live
  path (no additional handle and no widened access mask - the existing
  `PROCESS_QUERY_LIMITED_INFORMATION` already covers it) and from the ETW event
  header on `ProcessStart`. Rundown (`DCStart`) entries describe processes that
  already existed when the session attached and carry no creation time, so theirs is
  recorded as unknown rather than invented.
- JSONL chain nodes gain an optional `created_utc` field (ISO-8601 UTC, omitted when
  unknown) so the ordering invariant can be re-checked from the logs after the fact.

### Changed

- The CSV header is **unchanged**: creation time is per-chain-node, and the CSV
  renders the whole chain into a single flattened cell, so a new column has no
  sensible per-node meaning. JSONL remains the lossless representation. Chain-cell
  *content* can now contain the `<parent exited, PID reused>` marker where a chain
  was truncated - the marker is written to the node's basename deliberately, so the
  truncation is visible in the line-oriented formats (CSV, logfmt, Markdown, plain
  text) and not only in JSONL.

## [1.0.0] - TBD

Initial public release.

### Added

- Native-AOT single-binary Windows 11 CLI (~11 MB), elevation required.
- `watch` subcommand: live classification of involuntary focus changes
  (`STEAL`, `MAYBE_STEAL`, `SHELL_TRANSIENT`, `PREV_WINDOW_CLOSED`,
  `FOCUS_RESTORED`, `SESSION_LOCK`, `SAME_APP`) plus user-driven categories.
- ETW NT Kernel Logger spawner attribution: chain walker reads spawn-time
  command lines and walks past `<exited>` PIDs to attribute short-lived popups.
- Parent process chain (PID, image path, command line, cwd) up to
  `--max-chain-depth`.
- Per-chain-node Windows session ID for multi-session forensics.
- Focused window's HMONITOR captured for multi-monitor attribution.
- Six exporters: CSV, JSONL, logfmt, Markdown, plain log, and the standalone
  HTML report; selectable via `--format`.
- UTC date-rollover rotation for file exporters.
- `--ignore-class`, `--ignore-image`, and `--ignore-child-of` glob filters.
- Built-in `SHELL_TRANSIENT` classifier, extensible via `--shell-class` and
  switchable off with `--no-shell-classify`.
- Per-source thresholds: `--threshold-ms`, `--threshold-alt-tab-ms`,
  `--threshold-click-ms` (5000 ms default for slow-following popups), and
  `--threshold-other-ms`.
- `--steal-idle` configurable idle window for the `STEAL` / `MAYBE_STEAL` split.
- `interactive` / `silent` / `status-only` UI modes; verbosity levels `0`-`2`.
- `--duration` and `--max-steals` bounded-run controls.
- ETW kernel drop counters surfaced in the `-v 2` exit summary.
- HTML report written on shutdown when `--format html` is selected.
- `version` subcommand prints banner + git short SHA + checks GitHub Releases
  for a newer version.
- Console title and bare-invocation banner reflect the current version.
- Update-check against GitHub Releases with a 24 h disk-cache, surfaced
  quietly during `watch` startup. Opt-out via `SPAWNSPOTTER_NO_UPDATE_CHECK`.

### Security

- CSV exporter neutralizes leading `=`, `+`, `-`, `@`, tab, and CR per OWASP
  formula-injection guidance.
- HTML report escapes every dynamic field before emission.
- Keyboard hook reads `vkCode` only for category classification; specific
  keystroke information is discarded inside the callback.

[Unreleased]: https://github.com/SixFive7/SpawnSpotter/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/SixFive7/SpawnSpotter/releases/tag/v1.0.0
