using SpawnSpotter.Events;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Walks a parent chain upward from its last node, enforcing the creation-time ordering
/// invariant from <see cref="ParentLinkVerifier"/> on every link.
///
/// <para>
/// Resolution is injected as <see cref="ResolveAncestor"/> so the two production sources - a
/// live <c>OpenProcess</c> snapshot and the ETW-fed <see cref="ProcessSpawnRegistry"/> - meet
/// the same single check. Keeping one check site is deliberate: duplicating it per source is
/// how one path silently drifts out of compliance, and the ETW path is the one that produced
/// the reported fabrication.
/// </para>
/// </summary>
internal static class ChainWalker
{
    /// <summary>Image path used when a PID resolves to nothing at all.</summary>
    internal const string UnresolvedImageMarker = "<exited or access denied>";

    /// <summary>Note on the terminal node for a PID that resolves to nothing at all.</summary>
    internal const string UnresolvedNote = "OpenProcess failed";

    /// <summary>
    /// Resolve <paramref name="pid"/> to a candidate ancestor node, or null when nothing is
    /// known about it (no live handle and no ETW record). Implementations must NOT apply the
    /// ordering invariant - that is the walker's job.
    /// </summary>
    internal delegate ChainNode? ResolveAncestor(uint pid);

    /// <summary>
    /// Append ancestors to <paramref name="chain"/>, starting from the parent PID of its last
    /// node, until the chain hits <paramref name="maxDepth"/>, reaches the idle/system process,
    /// revisits a PID, fails to resolve, or is truncated for PID reuse.
    /// </summary>
    internal static void Walk(List<ChainNode> chain, int maxDepth, ResolveAncestor resolve)
    {
        if (chain.Count == 0) { return; }
        var nextPid = chain[^1].ParentPid;
        var seen = new HashSet<uint>(8);
        foreach (var n in chain) { seen.Add(n.Pid); }

        while (chain.Count < maxDepth && nextPid != 0 && nextPid != 4 && !seen.Contains(nextPid))
        {
            var candidate = resolve(nextPid);
            if (candidate is null)
            {
                chain.Add(new ChainNode(
                    Pid: nextPid,
                    ImagePath: UnresolvedImageMarker,
                    ImageBasename: string.Empty,
                    CommandLine: string.Empty,
                    CurrentDirectory: string.Empty,
                    PackageAumi: null,
                    Environment: null,
                    ParentPid: 0,
                    Note: UnresolvedNote));
                break;
            }

            switch (ParentLinkVerifier.Check(chain[^1].CreateTimeUtc, candidate.CreateTimeUtc))
            {
                case ParentLinkVerdict.PidReused:
                    // Provably not the creator: this PID was recycled after the child was born.
                    // Stop rather than adopt this stranger's ancestors as our own.
                    chain.Add(ParentLinkVerifier.TruncationNode(nextPid));
                    return;

                case ParentLinkVerdict.Unverified:
                    // Not provably wrong, so keep it - truncating on "unknown" would discard
                    // correct chains - but say so, so the log distinguishes checked from unchecked.
                    candidate = candidate with { Note = ParentLinkVerifier.AnnotateUnverified(candidate.Note) };
                    break;
            }

            chain.Add(candidate);
            // Guard on both: nextPid drives the loop, candidate.Pid is what the node claims to be.
            // In production they are always equal; belt and braces keeps the cycle guard honest.
            seen.Add(nextPid);
            seen.Add(candidate.Pid);
            nextPid = candidate.ParentPid;
        }
    }
}
