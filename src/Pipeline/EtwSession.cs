using System.Runtime.InteropServices;
using SpawnSpotter.Native;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Owns the system-wide real-time <c>NT Kernel Logger</c> session, configured to emit classic
/// (MOF) Process events that carry the full command line at creation. The session is created in
/// <see cref="Start"/>; the consumer thread attaches via <c>OpenTraceW</c> + <c>ProcessTrace</c>
/// to drain events into <c>ProcessSpawnRegistry</c>.
///
/// <para>
/// Lifecycle (hard fail on session errors): any failure in <see cref="Start"/> throws
/// <see cref="EtwSessionException"/>. The Runner catches it, prints a clear message, and
/// exits non-zero. There is no automatic recovery - the user re-runs once the conflicting
/// session is gone.
/// </para>
///
/// <para>
/// Session name: <c>NT Kernel Logger</c>. This is a single, system-wide singleton session -
/// only one consumer (any process) may own it at a time. A conflicting owner makes
/// <c>StartTraceW</c> fail with <see cref="Etw.ERROR_ALREADY_EXISTS"/>, which we hard-fail with
/// cleanup guidance (<c>logman stop "NT Kernel Logger" -ets</c>). <see cref="TryStopByName"/>
/// best-effort reclaims a leftover instance from a prior crashed run before starting.
/// </para>
/// </summary>
internal sealed unsafe class EtwSession : IDisposable
{
    public string SessionName { get; }

    /// <summary>Kernel-assigned trace handle. Valid only after <see cref="Start"/> succeeded.</summary>
    public ulong TraceHandle => _traceHandle;

    /// <summary>
    /// Kernel-side drop counters, populated by <see cref="Stop"/> from the
    /// EVENT_TRACE_PROPERTIES OUT fields. Zero until Stop runs.
    ///
    /// <para><c>EventsLost</c>: events the kernel dropped because the real-time consumer (us)
    /// could not keep up. <c>RealTimeBuffersLost</c>: entire kernel buffers dropped under the same
    /// pressure. <c>LogBuffersLost</c>: buffers lost from session resource limits.</para>
    /// </summary>
    public uint EventsLost { get; private set; }
    public uint RealTimeBuffersLost { get; private set; }
    public uint LogBuffersLost { get; private set; }

    private ulong _traceHandle;
    private IntPtr _propertiesBuffer;     // owns the EVENT_TRACE_PROPERTIES + trailing name allocation
    private int _propertiesBufferSize;
    private bool _started;
    private bool _disposed;

    /// <summary>Reasonable buffer/timing defaults for &lt;1000 events/sec process activity.</summary>
    private const uint BufferSizeKb = 64;
    private const uint MinimumBuffers = 4;
    private const uint MaximumBuffers = 16;
    private const uint FlushTimerSeconds = 1;
    private const int TrailingNameBytes = 1024; // plenty for "NT Kernel Logger\0"

    public EtwSession() : this(Etw.KERNEL_LOGGER_NAME) { }

    public EtwSession(string sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            throw new ArgumentException("Session name must be non-empty.", nameof(sessionName));
        }
        SessionName = sessionName;
    }

    /// <summary>
    /// Creates the NT Kernel Logger session with classic Process events enabled. The kernel
    /// logger is configured entirely through <c>StartTraceW</c> (its <see cref="Etw.WNODE_HEADER.Guid"/>
    /// selects the singleton session and <see cref="Etw.EVENT_TRACE_PROPERTIES.EnableFlags"/> selects
    /// the event groups) - there is no separate provider-enablement step. Throws
    /// <see cref="EtwSessionException"/> on any failure - caller is expected to catch
    /// and fail the run cleanly.
    /// </summary>
    public void Start()
    {
        if (_started) { throw new InvalidOperationException("EtwSession already started."); }
        if (_disposed) { throw new ObjectDisposedException(nameof(EtwSession)); }

        // 1) Best-effort cleanup of any leftover session with the same name (rare - only if
        // a prior process with the same pid crashed without unhooking). We swallow errors:
        // if there is no such session, StopByName returns ERROR_WMI_INSTANCE_NOT_FOUND
        // which is the expected case on first run.
        TryStopByName(SessionName);

        // 2) Allocate the EVENT_TRACE_PROPERTIES buffer + trailing space for the session name.
        var totalSize = Etw.SizeOfEventTraceProperties + TrailingNameBytes;
        _propertiesBufferSize = totalSize;
        _propertiesBuffer = Marshal.AllocHGlobal(totalSize);
        NativeMemory.Clear((void*)_propertiesBuffer, (nuint)totalSize);

        var props = (Etw.EVENT_TRACE_PROPERTIES*)_propertiesBuffer;
        props->Wnode.BufferSize = (uint)totalSize;
        props->Wnode.ClientContext = Etw.WNODE_CLIENT_CONTEXT_QPC;
        props->Wnode.Flags = Etw.WNODE_FLAG_TRACED_GUID;
        // The NT Kernel Logger is selected by writing SystemTraceControlGuid into the WNODE
        // GUID, and its event groups are chosen via EnableFlags - both BEFORE StartTraceW.
        props->Wnode.Guid = Etw.SystemTraceControlGuid;
        props->EnableFlags = Etw.EVENT_TRACE_FLAG_PROCESS;
        props->BufferSize = BufferSizeKb;
        props->MinimumBuffers = MinimumBuffers;
        props->MaximumBuffers = MaximumBuffers;
        props->FlushTimer = FlushTimerSeconds;
        // Real-time only. The NT Kernel Logger logs from non-paged contexts, so
        // EVENT_TRACE_USE_PAGED_MEMORY is invalid here (StartTraceW -> ERROR_INVALID_PARAMETER).
        props->LogFileMode = Etw.EVENT_TRACE_REAL_TIME_MODE;
        props->LogFileNameOffset = 0;
        props->LoggerNameOffset = (uint)Etw.SizeOfEventTraceProperties;
        // Kernel copies SessionName back into [LoggerNameOffset..] on success - we don't
        // need to pre-fill it (StartTraceW takes the InstanceName separately as a string).

        // 3) Create the session. For the kernel logger this fully configures it (Process events
        // via EnableFlags); there is no separate provider-enablement step.
        var startResult = Etw.StartTraceW(out _traceHandle, SessionName, props);
        if (startResult != Etw.ERROR_SUCCESS)
        {
            FreeBuffer();
            throw new EtwSessionException(
                $"StartTraceW failed for session '{SessionName}': Win32 error 0x{startResult:X} ({FormatEtwError(startResult)}).");
        }

        _started = true;
    }

    /// <summary>
    /// Idempotent. Stops the session cleanly. Safe to call from a Ctrl+C handler, an outer
    /// <c>finally</c> block, or an <see cref="AppDomain.ProcessExit"/> hook - the
    /// <c>_started</c> guard makes the second invocation a cheap no-op, so wiring both a
    /// finally-driven teardown AND a ProcessExit safety net is fine. Errors from
    /// <c>ControlTraceW</c> are logged to stderr and swallowed - teardown is best-effort
    /// because the priority is releasing the NT Kernel Logger singleton (anything we can do
    /// to avoid leaking it across runs).
    /// </summary>
    public void Stop()
    {
        if (!_started) { return; }
        try
        {
            TryStopByHandle();
        }
        finally
        {
            _traceHandle = 0;
            _started = false;
            FreeBuffer();
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        Stop();
    }

    // -------------------------------------------------------------------------

    private void TryStopByHandle()
    {
        if (_traceHandle == 0 || _propertiesBuffer == IntPtr.Zero) { return; }
        var props = (Etw.EVENT_TRACE_PROPERTIES*)_propertiesBuffer;
        var code = Etw.ControlTraceW(_traceHandle, null, props, Etw.EVENT_TRACE_CONTROL_STOP);
        if (code != Etw.ERROR_SUCCESS && code != Etw.ERROR_WMI_INSTANCE_NOT_FOUND)
        {
            Console.Error.WriteLine(
                $"[ETW] ControlTraceW(STOP) returned 0x{code:X} ({FormatEtwError(code)}) for session '{SessionName}'. Session may be leaked - clean up with: logman stop \"{SessionName}\" -ets");
            return;
        }
        // On a successful stop, the kernel populates the EVENT_TRACE_PROPERTIES OUT fields. Snapshot
        // them now - the buffer is freed in Stop()'s finally and we want the counters available to
        // the exit summary regardless of which Stop / Dispose path ran us.
        EventsLost = props->EventsLost;
        RealTimeBuffersLost = props->RealTimeBuffersLost;
        LogBuffersLost = props->LogBuffersLost;
    }

    /// <summary>
    /// Best-effort cleanup of a leftover session with this name. ControlTraceW(STOP) by
    /// name needs its own EVENT_TRACE_PROPERTIES buffer; the call has tight requirements
    /// on <see cref="Etw.WNODE_HEADER.BufferSize"/> and <see cref="Etw.EVENT_TRACE_PROPERTIES.LoggerNameOffset"/>
    /// matching the original. We allocate a fresh one for this purpose only.
    /// </summary>
    private static void TryStopByName(string sessionName)
    {
        var totalSize = Etw.SizeOfEventTraceProperties + TrailingNameBytes;
        var buf = Marshal.AllocHGlobal(totalSize);
        try
        {
            NativeMemory.Clear((void*)buf, (nuint)totalSize);
            var props = (Etw.EVENT_TRACE_PROPERTIES*)buf;
            props->Wnode.BufferSize = (uint)totalSize;
            props->LoggerNameOffset = (uint)Etw.SizeOfEventTraceProperties;
            // TraceHandle=0 with a non-null InstanceName means "look up by name".
            _ = Etw.ControlTraceW(0, sessionName, props, Etw.EVENT_TRACE_CONTROL_STOP);
            // Ignore the return value - ERROR_WMI_INSTANCE_NOT_FOUND is the normal "no leftover" path.
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private void FreeBuffer()
    {
        if (_propertiesBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_propertiesBuffer);
            _propertiesBuffer = IntPtr.Zero;
            _propertiesBufferSize = 0;
        }
    }

    private static string FormatEtwError(int code) => code switch
    {
        Etw.ERROR_ACCESS_DENIED => "ACCESS_DENIED - the NT Kernel Logger needs administrator",
        Etw.ERROR_ALREADY_EXISTS => "ALREADY_EXISTS - the NT Kernel Logger is already owned by another consumer; stop it with: logman stop \"NT Kernel Logger\" -ets",
        Etw.ERROR_INVALID_PARAMETER => "INVALID_PARAMETER",
        Etw.ERROR_BAD_LENGTH => "BAD_LENGTH",
        Etw.ERROR_WMI_INSTANCE_NOT_FOUND => "WMI_INSTANCE_NOT_FOUND",
        _ => "unknown",
    };
}

/// <summary>
/// Thrown by <see cref="EtwSession.Start"/> on any session failure. Hard-fail policy:
/// the Runner catches this, prints the message, and exits with a non-zero code.
/// </summary>
internal sealed class EtwSessionException : Exception
{
    public EtwSessionException(string message) : base(message) { }
    public EtwSessionException(string message, Exception inner) : base(message, inner) { }
}
