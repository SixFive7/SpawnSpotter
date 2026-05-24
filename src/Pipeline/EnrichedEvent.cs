using SpawnSpotter.Events;
using SpawnSpotter.Process;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Output of the enrichment <see cref="System.Threading.Tasks.Dataflow.TransformBlock{TInput,TOutput}"/>:
/// everything the classifier and exporters need. Built from a <see cref="RawHookEvent"/> by
/// <see cref="EnrichmentPipeline"/> using Win32 calls + <see cref="ProcessReader.TrySnapshot"/>
/// for focused, parent, and walked ancestors.
///
/// Carries forward the contents of the (now-deleted) <c>RawEvent</c>.
/// </summary>
internal readonly record struct EnrichedEvent(
    long Seq,
    long TickMs,
    DateTime WallUtc,
    HookEventKind Kind,
    IntPtr Hwnd,
    uint EventType,
    uint FocusedPid,
    string WindowClass,
    string WindowTitle,
    ProcessSnapshot? FocusedSnapshot,
    ProcessSnapshot? ParentSnapshot,
    IReadOnlyList<ChainNode> AncestorChain);
