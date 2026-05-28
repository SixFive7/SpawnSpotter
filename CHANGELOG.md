# Changelog

All notable changes to SpawnSpotter are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and SpawnSpotter
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`MAJOR.MINOR.PATCH` bumps are driven by the user-visible CLI surface:

- **MAJOR** - breaking change to a CLI flag, exit code, log schema, or default behavior.
- **MINOR** - new feature, new flag, or new event classification.
- **PATCH** - bug fix or doc-only change with no observable surface change.

## [Unreleased]

_No unreleased changes._

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
