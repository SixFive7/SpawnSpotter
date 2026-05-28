using SpawnSpotter.Events;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Discriminates what kind of observation a hook callback produced. The classifier branches on
/// this enum to decide how to handle the event: window events get enriched + classified; input
/// events update sink-local last-X timestamps; pipeline-pressure events emit a special record.
///
/// <para>
/// Privacy: the keyboard hook callback consumes the raw vkCode, categorizes it, decides whether
/// the event is a recognized gesture (Alt+Tab release, System-key release), and emits one of
/// the <c>Input*</c> kinds below. The vkCode never leaves the callback. The kind alone tells
/// the classifier "user did something" without telling it WHICH key.
/// </para>
/// </summary>
internal enum HookEventKind : byte
{
    // Window events - carry an HWND and an event type; need enrichment downstream.
    Foreground = 1,
    ObjectShow = 2,
    ObjectFocus = 3,

    // Input events - carry only a timestamp; update last-X tick in the classifier.
    InputKeyDown = 4,               // any keydown - updates LastKeyTickMs
    InputAltTabReleased = 5,        // Tab released while Alt held - updates LastAltTabReleaseTickMs
    InputSystemKeyReleased = 6,     // System-category key released - updates LastSystemKeyReleaseTickMs
    InputMouseButtonDown = 7,       // any mouse button pressed - updates LastMouseDownTickMs

    // System / meta events - emitted by the enricher stage in Phase 3.
    PipelinePressureEnter = 8,      // BufferBlock crossed 90% full
    PipelinePressureClear = 9,      // BufferBlock dropped back below 70% full
}

/// <summary>
/// What a hook callback produces. Carries the monotonic sequence + timestamps + the
/// discriminating kind. Window-specific fields (<see cref="Hwnd"/>, <see cref="EventType"/>)
/// are zero/default for input and pressure events. Pressure events carry a <see cref="Note"/>
/// describing the buffer state.
///
/// Hook-callback budget target: under 5 microseconds for construction + post.
/// </summary>
internal readonly record struct RawHookEvent(
    long Seq,
    long TickMs,
    DateTime WallUtc,
    HookEventKind Kind,
    IntPtr Hwnd,
    uint EventType,
    string? Note,
    bool ModifierHeld = false);

internal static class HookEventKindExtensions
{
    public static MonitoredVia ToMonitoredVia(this HookEventKind k) => k switch
    {
        HookEventKind.Foreground => MonitoredVia.SystemForeground,
        HookEventKind.ObjectShow => MonitoredVia.ObjectShow,
        HookEventKind.ObjectFocus => MonitoredVia.ObjectFocus,
        _ => MonitoredVia.SystemForeground,
    };

    public static bool IsWindowEvent(this HookEventKind k) =>
        k is HookEventKind.Foreground or HookEventKind.ObjectShow or HookEventKind.ObjectFocus;

    public static bool IsInputEvent(this HookEventKind k) =>
        k is HookEventKind.InputKeyDown
          or HookEventKind.InputAltTabReleased
          or HookEventKind.InputSystemKeyReleased
          or HookEventKind.InputMouseButtonDown;

    public static bool IsPressureEvent(this HookEventKind k) =>
        k is HookEventKind.PipelinePressureEnter or HookEventKind.PipelinePressureClear;
}
