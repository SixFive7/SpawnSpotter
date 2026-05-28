# SpawnSpotter — TODO

Open repo-level tasks. Cross-cutting items only; per-feature work belongs in code
comments or commit messages.

## Versioning: pin a real version number

**Current state.** `SpawnSpotter.csproj` does not set `<Version>` / `<VersionPrefix>` /
`<AssemblyVersion>`, so MSBuild falls back to the SDK default `1.0.0`. The CSC-emitted
`AssemblyInformationalVersionAttribute` ends up as `1.0.0+<git-sha>` (the suffix is
auto-attached because `Microsoft.NET.Sdk` picks up `SourceRevisionId` from the git
commit). `VersionCommand` (`src/Cli/VersionCommand.cs`) reads
`InformationalVersion`, so today `spawnspotter version` prints `SpawnSpotter 1.0.0+<sha>`.

The README claims `version` prints "version + git commit and exit." That's literally
true — there's a version, and there's a commit — but the version number is permanently
`1.0.0` and will never change as the project evolves. That's the gap.

**Options (not yet picked):**

1. **Set `<Version>` manually in `SpawnSpotter.csproj`.** Bump it by hand on each
   release. Smallest change, no tooling. Easy to forget; risks drifting from git tags.
2. **Adopt [MinVer](https://github.com/adamralph/minver).** Tag-driven SemVer; reads
   `git describe --tags` at build time. One package reference, zero per-release work
   once tagged. Works under Native AOT (build-time only).
3. **Adopt [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning).**
   Heavier (a `version.json` + cloud-CI integration story), but supports
   build-height suffixes for non-tag commits and per-branch version policies.
4. **Live with the gap.** Keep the version at `1.0.0`; rely entirely on the commit
   suffix for identification. Cheapest. Defensible if releases are de facto
   "whatever's on `main`".

Decision deferred — pick one when the release cadence is clearer.
