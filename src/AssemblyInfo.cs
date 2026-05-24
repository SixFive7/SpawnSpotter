using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

[assembly: InternalsVisibleTo("SpawnSpotter.Tests")]

// AOT/perf: disable runtime marshalling. All P/Invoke goes through source-generated marshaling
// (plan section 3). This drops the blittable-only constraint on some signatures and makes the
// produced AOT code smaller and slightly faster.
[assembly: DisableRuntimeMarshalling]

// We only run on Windows. Skips CA1416 noise for every Win32 P/Invoke caller.
[assembly: SupportedOSPlatform("windows")]
