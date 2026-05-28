using System.Runtime.InteropServices;

namespace SpawnSpotter.Native;

/// <summary>
/// ETW (Event Tracing for Windows) <c>[LibraryImport]</c> P/Invoke declarations and minimal
/// struct layouts. Used by <see cref="Pipeline.EtwSession"/> to control the system-wide
/// real-time <c>NT Kernel Logger</c> session, which emits classic (MOF) Process events that
/// carry the full command line at creation — race-free, unlike a post-spawn user-mode query.
///
/// <para>
/// Plan §3 mandates hand-rolled P/Invoke + <c>[LibraryImport]</c> (no TraceEvent NuGet,
/// no <c>[DllImport]</c>). All structs are blittable so the assembly-level
/// <c>[DisableRuntimeMarshalling]</c> applies.
/// </para>
/// </summary>
internal static partial class Etw
{
    // =========================================================================
    // Provider GUIDs
    // =========================================================================

    /// <summary>
    /// <c>Microsoft-Windows-Kernel-Process</c> — public ETW manifest provider that emits
    /// ProcessStart (id 1), ProcessStop (id 2), ProcessRundown (id 15), ThreadStart (id 3),
    /// ImageLoad (id 5), and friends. Documented but does NOT emit the command line. Retained
    /// only for reference — the active session uses the NT Kernel Logger (below) instead.
    /// </summary>
    public static readonly Guid KernelProcessProviderGuid =
        new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");

    /// <summary>
    /// <c>SystemTraceControlGuid</c> — the well-known session GUID that selects the singleton
    /// <c>NT Kernel Logger</c> kernel session. Written to <see cref="WNODE_HEADER.Guid"/> in
    /// <c>StartTraceW</c>; combined with <see cref="EVENT_TRACE_FLAG_PROCESS"/> in
    /// <see cref="EVENT_TRACE_PROPERTIES.EnableFlags"/> it turns on classic Process events.
    /// </summary>
    public static readonly Guid SystemTraceControlGuid =
        new("9e814aad-3204-11d2-9a82-006008a86939");

    /// <summary>
    /// <c>EventTraceProcessGuid</c> — the MOF event-class GUID stamped into
    /// <c>EVENT_RECORD.EventHeader.ProviderId</c> for every classic kernel Process event
    /// (Start / End / DCStart / DCEnd). We discriminate kernel Process events by this GUID,
    /// NOT by <c>EventDescriptor.Id</c> (classic MOF events carry Id = 0).
    /// </summary>
    public static readonly Guid EventTraceProcessGuid =
        new("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");

    /// <summary>
    /// <see cref="EVENT_TRACE_PROPERTIES.EnableFlags"/> bit that enables classic Process
    /// (and process-rundown) events on the NT Kernel Logger session.
    /// </summary>
    public const uint EVENT_TRACE_FLAG_PROCESS = 0x00000001;

    /// <summary>
    /// The fixed name of the singleton kernel logger session. Only one such session can exist
    /// system-wide; a second <c>StartTraceW</c> for it fails with <see cref="ERROR_ALREADY_EXISTS"/>.
    /// </summary>
    public const string KERNEL_LOGGER_NAME = "NT Kernel Logger";

    // =========================================================================
    // Constants
    // =========================================================================

    /// <summary>Real-time mode: events delivered live to a consumer, not written to a file.</summary>
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;

    /// <summary>Use pageable buffers (less critical memory pressure on the system).</summary>
    public const uint EVENT_TRACE_USE_PAGED_MEMORY = 0x01000000;

    /// <summary>QueryPerformanceCounter — highest-resolution timestamps.</summary>
    public const uint WNODE_CLIENT_CONTEXT_QPC = 1;

    /// <summary>Required <see cref="WNODE_HEADER.Flags"/> bit for trace sessions.</summary>
    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;

    // ControlTraceW codes
    public const uint EVENT_TRACE_CONTROL_QUERY = 0;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;
    public const uint EVENT_TRACE_CONTROL_FLUSH = 3;

    // EnableTraceEx2 codes
    public const uint EVENT_CONTROL_CODE_DISABLE_PROVIDER = 0;
    public const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;

    public const byte TRACE_LEVEL_INFORMATION = 4;

    /// <summary>
    /// Keyword bit for <c>Microsoft-Windows-Kernel-Process</c>'s <c>WINEVENT_KEYWORD_PROCESS</c>:
    /// enables only the Process / Thread / Image events (not the full kernel-process firehose).
    /// </summary>
    public const ulong KERNEL_PROCESS_KEYWORD_PROCESS = 0x0010;

    // Win32 error codes we surface as friendly messages
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_BAD_LENGTH = 24;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_WMI_INSTANCE_NOT_FOUND = 4201;

    // =========================================================================
    // Structs — session control
    // =========================================================================

    /// <summary>
    /// Fixed prefix of <see cref="EVENT_TRACE_PROPERTIES"/>. 48 bytes on x64; sets the trace's
    /// clock source (QPC), provider GUID (kernel-assigned for non-system loggers), and total
    /// buffer size including the trailing session-name space.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WNODE_HEADER
    {
        public uint BufferSize;        // total bytes (header + properties + trailing names)
        public uint ProviderId;        // unused for user-mode loggers
        public ulong HistoricalContext;// union: { Version, Linkage } — leave zero
        public long TimeStamp;         // union: KernelHandle — leave zero
        public Guid Guid;              // session GUID — leave zero (kernel assigns)
        public uint ClientContext;     // 1 = QPC clock
        public uint Flags;             // WNODE_FLAG_TRACED_GUID
    }

    /// <summary>
    /// The fixed part of the ETW session control struct (120 bytes on x64). Allocated together
    /// with trailing space for the session name; <see cref="LoggerNameOffset"/> points there.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;            // per-buffer size in KB
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;       // 0 = no cap (real-time only)
        public uint LogFileMode;           // EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_USE_PAGED_MEMORY
        public uint FlushTimer;            // seconds; 0 = default
        public uint EnableFlags;           // NT Kernel Logger group mask — EVENT_TRACE_FLAG_PROCESS
        public int AgeLimit;               // union: { FlushThreshold } — leave zero
        public uint NumberOfBuffers;       // [out]
        public uint FreeBuffers;           // [out]
        public uint EventsLost;            // [out]
        public uint BuffersWritten;        // [out]
        public uint LogBuffersLost;        // [out]
        public uint RealTimeBuffersLost;   // [out]
        public IntPtr LoggerThreadId;      // [out]
        public uint LogFileNameOffset;     // 0 — real-time, no file
        public uint LoggerNameOffset;      // sizeof(EVENT_TRACE_PROPERTIES) — points at trailing name buffer
    }

    /// <summary>Size of the fixed prefix of <see cref="EVENT_TRACE_PROPERTIES"/> in bytes.
    /// Equals 120 on x64 — used by the session allocator to position the trailing name buffer
    /// at the offset stored in <see cref="EVENT_TRACE_PROPERTIES.LoggerNameOffset"/>.</summary>
    public static unsafe int SizeOfEventTraceProperties => sizeof(EVENT_TRACE_PROPERTIES);

    // =========================================================================
    // P/Invokes — session control
    // =========================================================================

    /// <summary>
    /// <c>StartTraceW</c> — create a private real-time ETW session.
    /// Returns 0 on success or a Win32 error code (e.g. <see cref="ERROR_ALREADY_EXISTS"/>,
    /// <see cref="ERROR_ACCESS_DENIED"/>).
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = false)]
    public static unsafe partial int StartTraceW(
        out ulong TraceHandle,
        string InstanceName,
        EVENT_TRACE_PROPERTIES* Properties);

    /// <summary>
    /// <c>ControlTraceW</c> — stop / flush / query a running session by name or handle.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = false)]
    public static unsafe partial int ControlTraceW(
        ulong TraceHandle,
        string? InstanceName,
        EVENT_TRACE_PROPERTIES* Properties,
        uint ControlCode);

    /// <summary>
    /// <c>EnableTraceEx2</c> — attach a provider GUID to an existing session.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "EnableTraceEx2", SetLastError = false)]
    public static unsafe partial int EnableTraceEx2(
        ulong TraceHandle,
        Guid* ProviderId,
        uint ControlCode,
        byte Level,
        ulong MatchAnyKeyword,
        ulong MatchAllKeyword,
        uint Timeout,
        IntPtr EnableParameters);

    // =========================================================================
    // Structs — consumer side
    // =========================================================================

    /// <summary>The fixed header on every <see cref="EVENT_RECORD"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_HEADER
    {
        public ushort Size;            // total event size (header + extended data + user data)
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;         // 100ns ticks since 1601 or QPC depending on session
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTimeOrKernelUserTime;  // union: ProcessorTime / { KernelTime, UserTime }
        public Guid ActivityId;
    }

    /// <summary>16-byte event identity from the manifest.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_DESCRIPTOR
    {
        public ushort Id;              // event ID per the manifest (1=ProcessStart, 2=ProcessStop, 15=Rundown)
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    /// <summary>Trailing buffer context block in each <see cref="EVENT_RECORD"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ETW_BUFFER_CONTEXT
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    /// <summary>
    /// One event delivered by <c>ProcessTrace</c> via the
    /// <see cref="EVENT_TRACE_LOGFILEW.EventRecordCallback"/>. UserData is a pointer to the
    /// payload (whose layout depends on the provider + EventDescriptor.Id + Version).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;       // bytes of the trailing payload at UserData
        public IntPtr ExtendedData;          // pointer to extended data items
        public IntPtr UserData;              // pointer to payload bytes
        public IntPtr UserContext;
    }

    /// <summary>
    /// Argument to <c>OpenTraceW</c>. We use the real-time mode where <see cref="LoggerName"/>
    /// is the session name and <see cref="EventRecordCallback"/> receives one event per call.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EVENT_TRACE_LOGFILEW
    {
        public char* LogFileName;            // null for real-time mode
        public char* LoggerName;             // the session name we created
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;        // PROCESS_TRACE_MODE_*
        public EVENT_TRACE CurrentEvent;     // 240 bytes — unused but consumes space in the struct
        public TRACE_LOGFILE_HEADER LogFileHeader;
        public delegate* unmanaged[Stdcall]<EVENT_TRACE*, void> BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        // PEVENT_CALLBACK or PEVENT_RECORD_CALLBACK depending on Mode; we set Mode to use Record callback.
        public delegate* unmanaged[Stdcall]<EVENT_RECORD*, void> EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;

    /// <summary>Container for the legacy "current event" view. We never read it; included so
    /// <see cref="EVENT_TRACE_LOGFILEW"/> has the correct field offsets.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public byte Type;
        public byte Level;
        public ushort Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public uint StartBuffers;            // union with GUID
        public uint PointerSize;
        public uint EventsLost;
        public uint CpuSpeedInMHz;
        public char* LoggerName;
        public char* LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    // Must match Win32 TIME_ZONE_INFORMATION exactly (172 bytes, 4-byte aligned): the two name
    // fields are WCHAR[32] = 64 bytes each. Getting this wrong shrinks the enclosing
    // TRACE_LOGFILE_HEADER and shifts EVENT_TRACE_LOGFILEW.EventRecordCallback to the wrong offset,
    // so ProcessTrace ends up calling a garbage function pointer (access violation before any
    // managed callback runs). Using `ulong` here would also force 8-byte alignment, which is wrong.
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        public fixed char StandardName[32];   // WCHAR[32] = 64 bytes
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        public fixed char DaylightName[32];   // WCHAR[32] = 64 bytes
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    // =========================================================================
    // P/Invokes — consumer side
    // =========================================================================

    /// <summary>
    /// <c>OpenTraceW</c> — open a real-time trace handle bound to the session named in
    /// <see cref="EVENT_TRACE_LOGFILEW.LoggerName"/>. Returns <c>INVALID_PROCESSTRACE_HANDLE</c>
    /// (= 0xFFFFFFFFFFFFFFFF) on failure; check <c>Marshal.GetLastPInvokeError</c>.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    public static unsafe partial ulong OpenTraceW(EVENT_TRACE_LOGFILEW* Logfile);

    /// <summary>
    /// <c>ProcessTrace</c> — blocks the calling thread, pumping events into the configured
    /// callback until either every handle is closed or <c>EndTime</c> elapses.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace", SetLastError = false)]
    public static unsafe partial int ProcessTrace(
        ulong* HandleArray,
        uint HandleCount,
        IntPtr StartTime,
        IntPtr EndTime);

    /// <summary>
    /// <c>CloseTrace</c> — break out of <see cref="ProcessTrace"/>. Returns
    /// <c>ERROR_CTX_CLOSE_PENDING</c> while events are still being delivered.
    /// </summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CloseTrace", SetLastError = false)]
    public static partial int CloseTrace(ulong TraceHandle);

    public const ulong INVALID_PROCESSTRACE_HANDLE = 0xFFFFFFFFFFFFFFFF;
    public const int ERROR_CTX_CLOSE_PENDING = 7007;
}

