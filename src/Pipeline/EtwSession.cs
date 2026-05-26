using System.Runtime.InteropServices;
using SpawnSpotter.Native;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// Owns a private real-time ETW session that subscribes to
/// <c>Microsoft-Windows-Kernel-Process</c>. The session is created in <see cref="Start"/>;
/// the consumer thread (added in Phase 4) attaches via <c>OpenTraceW</c> + <c>ProcessTrace</c>
/// to drain events into <c>ProcessSpawnRegistry</c>.
///
/// <para>
/// Lifecycle (Q1a — hard fail on session errors): any failure in <see cref="Start"/> throws
/// <see cref="EtwSessionException"/>. The Runner catches it, prints a clear message, and
/// exits non-zero. There is no automatic recovery — the user re-runs once the conflicting
/// session is gone.
/// </para>
///
/// <para>
/// Session name: <c>SpawnSpotter-{pid}</c>. Including the pid keeps two concurrent runs from
/// colliding and makes leaked sessions from prior crashes trivially identifiable
/// (<c>logman query -ets | findstr SpawnSpotter</c>).
/// </para>
/// </summary>
internal sealed unsafe class EtwSession : IDisposable
{
    public string SessionName { get; }

    /// <summary>Kernel-assigned trace handle. Valid only after <see cref="Start"/> succeeded.</summary>
    public ulong TraceHandle => _traceHandle;

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
    private const int TrailingNameBytes = 1024; // plenty for "SpawnSpotter-{pid}\0"

    public EtwSession() : this($"SpawnSpotter-{Environment.ProcessId}") { }

    public EtwSession(string sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            throw new ArgumentException("Session name must be non-empty.", nameof(sessionName));
        }
        SessionName = sessionName;
    }

    /// <summary>
    /// Creates the ETW session and enables the kernel-process provider on it. Throws
    /// <see cref="EtwSessionException"/> on any failure — caller is expected to catch
    /// and fail the run cleanly.
    /// </summary>
    public void Start()
    {
        if (_started) { throw new InvalidOperationException("EtwSession already started."); }
        if (_disposed) { throw new ObjectDisposedException(nameof(EtwSession)); }

        // 1) Best-effort cleanup of any leftover session with the same name (rare — only if
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
        props->BufferSize = BufferSizeKb;
        props->MinimumBuffers = MinimumBuffers;
        props->MaximumBuffers = MaximumBuffers;
        props->FlushTimer = FlushTimerSeconds;
        props->LogFileMode = Etw.EVENT_TRACE_REAL_TIME_MODE | Etw.EVENT_TRACE_USE_PAGED_MEMORY;
        props->LogFileNameOffset = 0;
        props->LoggerNameOffset = (uint)Etw.SizeOfEventTraceProperties;
        // Kernel copies SessionName back into [LoggerNameOffset..] on success — we don't
        // need to pre-fill it (StartTraceW takes the InstanceName separately as a string).

        // 3) Create the session.
        var startResult = Etw.StartTraceW(out _traceHandle, SessionName, props);
        if (startResult != Etw.ERROR_SUCCESS)
        {
            FreeBuffer();
            throw new EtwSessionException(
                $"StartTraceW failed for session '{SessionName}': Win32 error 0x{startResult:X} ({FormatEtwError(startResult)}).");
        }

        // 4) Enable the kernel-process provider on the new session.
        var providerId = Etw.KernelProcessProviderGuid;
        var enableResult = Etw.EnableTraceEx2(
            _traceHandle,
            &providerId,
            Etw.EVENT_CONTROL_CODE_ENABLE_PROVIDER,
            Etw.TRACE_LEVEL_INFORMATION,
            Etw.KERNEL_PROCESS_KEYWORD_PROCESS,
            MatchAllKeyword: 0,
            Timeout: 0,
            EnableParameters: IntPtr.Zero);
        if (enableResult != Etw.ERROR_SUCCESS)
        {
            // Best-effort teardown so we don't leak a half-configured session.
            TryStopByHandle();
            FreeBuffer();
            _traceHandle = 0;
            throw new EtwSessionException(
                $"EnableTraceEx2 failed for provider Microsoft-Windows-Kernel-Process: Win32 error 0x{enableResult:X} ({FormatEtwError(enableResult)}).");
        }

        _started = true;
    }

    /// <summary>
    /// Idempotent. Stops the session cleanly. Safe to call from a Ctrl+C handler or
    /// <see cref="AppDomain.ProcessExit"/>. Errors are logged to stderr and swallowed —
    /// teardown best-effort.
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
                $"[ETW] ControlTraceW(STOP) returned 0x{code:X} ({FormatEtwError(code)}) for session '{SessionName}'. Session may be leaked — clean up with: logman stop \"{SessionName}\" -ets");
        }
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
            // Ignore the return value — ERROR_WMI_INSTANCE_NOT_FOUND is the normal "no leftover" path.
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
        Etw.ERROR_ACCESS_DENIED => "ACCESS_DENIED — session needs administrator",
        Etw.ERROR_ALREADY_EXISTS => "ALREADY_EXISTS — a session with this name is already running",
        Etw.ERROR_INVALID_PARAMETER => "INVALID_PARAMETER",
        Etw.ERROR_BAD_LENGTH => "BAD_LENGTH",
        Etw.ERROR_WMI_INSTANCE_NOT_FOUND => "WMI_INSTANCE_NOT_FOUND",
        _ => "unknown",
    };
}

/// <summary>
/// Thrown by <see cref="EtwSession.Start"/> on any session failure. Hard-fail per Q1a:
/// the Runner catches this, prints the message, and exits with a non-zero code.
/// </summary>
internal sealed class EtwSessionException : Exception
{
    public EtwSessionException(string message) : base(message) { }
    public EtwSessionException(string message, Exception inner) : base(message, inner) { }
}
