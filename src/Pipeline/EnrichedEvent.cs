using SpawnSpotter.Events;
using SpawnSpotter.Process;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Output of the enrichment <see cref="System.Threading.Tasks.Dataflow.TransformBlock{TInput,TOutput}"/>:
/// everything the classifier and exporters need.
///
/// <para>For window events (Foreground / ObjectShow / ObjectFocus): every field populated.</para>
/// <para>For input events (InputKeyDown / InputAltTabReleased / InputSystemKeyReleased /
/// InputMouseButtonDown): window-specific fields are default (Hwnd=0, empty strings, empty chain).
/// The classifier branches on <see cref="Kind"/> and only consults the window fields when relevant.</para>
/// <para>For pressure events (PipelinePressureEnter / PipelinePressureClear): only <see cref="Note"/>
/// and the timestamp fields are meaningful.</para>
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
    IReadOnlyList<ChainNode> AncestorChain,
    string? Note,
    bool ModifierHeld = false);
