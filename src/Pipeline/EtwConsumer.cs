using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpawnSpotter.Native;

namespace SpawnSpotter.Pipeline;

/// <summary>
/// The real-time consumer side of <see cref="EtwSession"/>. Owns the consumer trace handle
/// (<c>OpenTraceW</c>) and the dedicated thread that blocks inside <c>ProcessTrace</c>
/// dispatching <see cref="Etw.EVENT_RECORD"/>s to <see cref="EtwPayloadDecoder"/>.
///
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><see cref="Start"/> — open the consumer handle and spin up the worker thread.</item>
///   <item>Worker calls <c>ProcessTrace</c> in a tight loop; the OS delivers each event via
///   <see cref="EventRecordCallback"/>.</item>
///   <item><see cref="Stop"/> — call <c>CloseTrace</c> on the consumer handle; <c>ProcessTrace</c>
///   returns shortly after. Join the worker thread.</item>
/// </list>
/// </para>
///
/// <para>
/// Single-instance per process: <see cref="EventRecordCallback"/> is a static
/// <c>[UnmanagedCallersOnly]</c> entry point and dispatches to <see cref="s_registry"/>,
/// which is a static field. A second consumer would overwrite the registry pointer.
/// </para>
/// </summary>
internal sealed unsafe class EtwConsumer : IDisposable
{
    private static ProcessSpawnRegistry? s_registry;

    private readonly string _sessionName;
    private readonly ProcessSpawnRegistry _registry;
    private Thread? _worker;
    private ulong _consumerHandle = Etw.INVALID_PROCESSTRACE_HANDLE;
    private IntPtr _sessionNameBuffer;  // UTF-16 buffer holding the session name for OpenTraceW
    private bool _running;
    private bool _disposed;
    private volatile bool _isHealthy = true;

    /// <summary>
    /// True while the consumer thread is alive and dispatching events. Flips to false if
    /// <c>ProcessTrace</c> returns an unexpected status or the worker throws — i.e., the abnormal
    /// termination paths. A normal <see cref="Stop"/> does NOT flip this; the consumer is still
    /// "healthy" — it just shut down on request. Surface for the status line + exit summary so the
    /// user sees that chain-walk past-exit recovery has silently weakened mid-run.
    /// </summary>
    public bool IsHealthy => _isHealthy;

    public EtwConsumer(string sessionName, ProcessSpawnRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            throw new ArgumentException("Session name must be non-empty.", nameof(sessionName));
        }
        _sessionName = sessionName;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Open the consumer handle and start the worker thread. Throws
    /// <see cref="EtwSessionException"/> if <c>OpenTraceW</c> fails — hard-fail per Q1a so the
    /// Runner can exit cleanly.
    /// </summary>
    public void Start()
    {
        if (_running) { throw new InvalidOperationException("EtwConsumer already started."); }
        if (_disposed) { throw new ObjectDisposedException(nameof(EtwConsumer)); }
        if (s_registry is not null)
        {
            throw new InvalidOperationException("Another EtwConsumer is already active in this process.");
        }

        s_registry = _registry;

        // Pin the session name in unmanaged memory so OpenTraceW's stored pointer stays valid.
        _sessionNameBuffer = AllocUtf16(_sessionName);

        var logfile = default(Etw.EVENT_TRACE_LOGFILEW);
        logfile.LoggerName = (char*)_sessionNameBuffer;
        logfile.LogFileName = null; // real-time only
        logfile.ProcessTraceMode = Etw.PROCESS_TRACE_MODE_REAL_TIME | Etw.PROCESS_TRACE_MODE_EVENT_RECORD;
        logfile.EventRecordCallback = &EventRecordCallback;
        logfile.BufferCallback = null;

        var handle = Etw.OpenTraceW(&logfile);
        if (handle == Etw.INVALID_PROCESSTRACE_HANDLE)
        {
            var err = Marshal.GetLastPInvokeError();
            FreeBuffer();
            s_registry = null;
            throw new EtwSessionException(
                $"OpenTraceW failed for session '{_sessionName}': Win32 error 0x{err:X}. The session was created but cannot be attached to.");
        }
        _consumerHandle = handle;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "ETW-Consumer",
        };
        _running = true;
        _worker.Start();
    }

    /// <summary>
    /// Idempotent. Closes the consumer handle (unblocking <c>ProcessTrace</c>) and joins the
    /// worker thread. Safe to call from any thread.
    /// </summary>
    public void Stop()
    {
        if (!_running) { return; }
        _running = false;

        var handle = _consumerHandle;
        _consumerHandle = Etw.INVALID_PROCESSTRACE_HANDLE;
        if (handle != Etw.INVALID_PROCESSTRACE_HANDLE)
        {
            var code = Etw.CloseTrace(handle);
            // ERROR_CTX_CLOSE_PENDING means the trace is still draining; ProcessTrace will
            // return shortly. Anything else is unexpected but not fatal.
            if (code != Etw.ERROR_SUCCESS && code != Etw.ERROR_CTX_CLOSE_PENDING)
            {
                Console.Error.WriteLine($"[ETW] CloseTrace returned 0x{code:X} for session '{_sessionName}'.");
            }
        }

        // Wait up to 3 s for the worker. If it's stuck, abandon it — the process is exiting.
        try { _worker?.Join(TimeSpan.FromSeconds(3)); } catch { }
        _worker = null;

        FreeBuffer();
        s_registry = null;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        Stop();
    }

    private void WorkerLoop()
    {
        // ProcessTrace blocks until CloseTrace is called on every handle in the array.
        // We have exactly one handle.
        var handle = _consumerHandle;
        try
        {
            var rc = Etw.ProcessTrace(&handle, 1, IntPtr.Zero, IntPtr.Zero);
            if (rc != Etw.ERROR_SUCCESS && rc != Etw.ERROR_CTX_CLOSE_PENDING && _running)
            {
                Console.Error.WriteLine($"[ETW] ProcessTrace returned 0x{rc:X} for session '{_sessionName}'.");
                _isHealthy = false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ETW] Consumer thread crashed: {ex.Message}");
            _isHealthy = false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void EventRecordCallback(Etw.EVENT_RECORD* rec)
    {
        // Hot path — the NT Kernel Logger can emit tens of events/sec under load.
        // Quick out for null + missing-registry races (Stop racing with a pending event).
        if (rec == null) { return; }
        var registry = s_registry;
        if (registry is null) { return; }
        EtwPayloadDecoder.DispatchToRegistry(rec, registry, Environment.TickCount64);
    }

    private static IntPtr AllocUtf16(string s)
    {
        // Total bytes = (s.Length + 1) * sizeof(char). +1 for NUL terminator.
        var byteCount = (s.Length + 1) * sizeof(char);
        var buf = Marshal.AllocHGlobal(byteCount);
        var span = new Span<char>((void*)buf, s.Length + 1);
        s.AsSpan().CopyTo(span);
        span[s.Length] = '\0';
        return buf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FreeBuffer()
    {
        if (_sessionNameBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_sessionNameBuffer);
            _sessionNameBuffer = IntPtr.Zero;
        }
    }
}
