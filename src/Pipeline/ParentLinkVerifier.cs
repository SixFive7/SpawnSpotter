using SpawnSpotter.Events;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Outcome of checking one claimed parent -&gt; child link in a process chain.
/// </summary>
internal enum ParentLinkVerdict
{
    /// <summary>Both creation times known, and the parent is not younger than the child.</summary>
    Verified,

    /// <summary>
    /// At least one creation time is unknown, so the link can be neither proven nor disproven.
    /// </summary>
    Unverified,

    /// <summary>
    /// The candidate parent was provably created after its claimed child. A process cannot
    /// predate its own parent, so the PID was recycled and the link is a fabrication.
    /// </summary>
    PidReused,
}

/// <summary>
/// The ordering invariant that makes Windows PID reuse detectable.
///
/// <para>
/// A Windows PID identifies a <i>slot</i>, not a process. The kernel stamps a process's creator
/// PID at creation time (<c>PROCESS_BASIC_INFORMATION.InheritedFromUniqueProcessId</c>) and never
/// updates it, so once the creator exits that number is free for reuse and the stale reference
/// silently starts pointing at an unrelated process. Resolving "who holds this PID now" and
/// trusting the answer is what let a chain walk graft a stranger's whole ancestry onto an
/// unrelated app.
/// </para>
///
/// <para>
/// <b>Invariant: a parent's creation time must be less than or equal to its child's.</b> A
/// violation is proof of reuse. Everything here is pure so the rule can be tested without
/// touching a real process.
/// </para>
/// </summary>
internal static class ParentLinkVerifier
{
    /// <summary>Image path / basename used for the node that replaces a rejected ancestor.</summary>
    internal const string ReusedImageMarker = "<parent exited, PID reused>";

    /// <summary>Machine-greppable note marking where a chain was cut for PID reuse.</summary>
    internal const string ReusedNote = "chain truncated: pid reused (candidate created after child)";

    /// <summary>Note suffix marking a link that could not be checked either way.</summary>
    internal const string UnverifiedNote = "parent link unverified (creation time unknown)";

    /// <summary>
    /// Check a claimed parent -&gt; child link by creation time.
    /// </summary>
    /// <param name="childCreatedUtc">Creation time of the node already in the chain.</param>
    /// <param name="candidateCreatedUtc">Creation time of the process now holding the parent PID.</param>
    internal static ParentLinkVerdict Check(DateTime? childCreatedUtc, DateTime? candidateCreatedUtc)
    {
        if (childCreatedUtc is not { } child || candidateCreatedUtc is not { } candidate)
        {
            // Unknown on either side proves nothing. Truncating here would throw away correct
            // chains, which is a worse failure than carrying an unverified one that says so.
            return ParentLinkVerdict.Unverified;
        }

        // Strictly greater-than: a parent and child created inside the same clock tick share a
        // timestamp, which is common for fast spawns and perfectly legitimate. Only a parent
        // that is genuinely younger than its child is impossible.
        return candidate > child ? ParentLinkVerdict.PidReused : ParentLinkVerdict.Verified;
    }

    /// <summary>Append the unverified marker to an existing note, preserving what was there.</summary>
    internal static string AnnotateUnverified(string? existingNote)
        => string.IsNullOrEmpty(existingNote) ? UnverifiedNote : existingNote + "; " + UnverifiedNote;

    /// <summary>
    /// The terminal node that replaces a rejected ancestor. <c>ParentPid: 0</c> stops the walk:
    /// once a link is known to be fabricated, everything above it would be someone else's
    /// ancestry.
    /// </summary>
    internal static ChainNode TruncationNode(uint pid) => new(
        Pid: pid,
        ImagePath: ReusedImageMarker,
        // Basename carries the marker too: the line-oriented exporters (CSV, logfmt, Markdown,
        // plain text) render chains from basenames only, and a truncation that is invisible in
        // five of six formats is not a truncation the analyst will ever notice.
        ImageBasename: ReusedImageMarker,
        CommandLine: string.Empty,
        CurrentDirectory: string.Empty,
        PackageAumi: null,
        Environment: null,
        ParentPid: 0,
        Note: ReusedNote);
}
