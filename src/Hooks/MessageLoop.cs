using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpawnSpotter.Native;

namespace SpawnSpotter.Hooks;

/// <summary>
/// STA thread that hosts the Win32 message loop, the hidden top-level window (for
/// <c>WM_DISPLAYCHANGE</c>/<c>WM_DPICHANGED</c>), and the five hooks (three WinEvent +
/// two low-level input). All hook callbacks are static <c>[UnmanagedCallersOnly]</c> methods,
/// addresses taken via <c>&amp;Callback</c> — no managed delegates, no GCHandle.Alloc pinning
/// (plan section 3, decision #28).
/// </summary>
internal static unsafe class MessageLoop
{
    private static readonly string ClassName =
        string.Create(CultureInfo.InvariantCulture, $"SpawnSpotter.MonitorWindow.{Environment.ProcessId}");

    private static Thread? s_thread;
    private static uint s_threadId;
    private static IntPtr s_hwnd;
    private static IntPtr s_hInstance;
    private static ushort s_classAtom;
    private static readonly ManualResetEventSlim s_ready = new(false);
    private static Exception? s_startupError;
    private static Action? s_onReady;
    private static Action? s_onTeardown;

    /// <summary>Set by <c>WM_DISPLAYCHANGE</c>/<c>WM_DPICHANGED</c>. Read by the classifier.</summary>
    public static long MonitorSuppressUntilTickMs;

    /// <summary>HWND of the hidden monitor window, used by tests and diagnostics.</summary>
    public static IntPtr Hwnd => s_hwnd;

    /// <summary>Win32 thread id of the message-loop thread.</summary>
    public static uint ThreadId => s_threadId;

    /// <summary>
    /// Starts the STA thread, registers the window class, creates the hidden top-level window,
    /// invokes <paramref name="onReady"/> on the STA thread (typically to install hooks — hooks
    /// MUST be installed on the thread that pumps their messages), then runs the message loop.
    /// On exit, <paramref name="onTeardown"/> is invoked on the STA thread before the window is
    /// destroyed (typically to uninstall hooks).
    ///
    /// <para>
    /// Returns once <paramref name="onReady"/> has completed (or throws if it failed). After
    /// this returns successfully, the STA thread is dispatching messages — hook callbacks fire.
    /// </para>
    /// </summary>
    public static void Start(Action? onReady = null, Action? onTeardown = null)
    {
        if (s_thread is not null)
        {
            throw new InvalidOperationException("MessageLoop already started.");
        }

        s_onReady = onReady;
        s_onTeardown = onTeardown;

        s_thread = new Thread(ThreadEntry)
        {
            Name = "SpawnSpotter.MessageLoop",
            IsBackground = true,
        };
        s_thread.SetApartmentState(ApartmentState.STA);
        s_thread.Start();

        s_ready.Wait();
        if (s_startupError is not null)
        {
            throw new InvalidOperationException("MessageLoop startup failed.", s_startupError);
        }
    }

    /// <summary>
    /// Cleanly stops the message loop: destroys the hidden window, unregisters the class,
    /// and posts WM_QUIT so the loop thread terminates.
    /// </summary>
    public static void Stop()
    {
        if (s_thread is null)
        {
            return;
        }

        // Ask the loop thread to tear down its window and exit.
        Win32.PostThreadMessageW(s_threadId, Win32Const.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        s_thread.Join();
        s_thread = null;
    }

    private static void ThreadEntry()
    {
        try
        {
            s_threadId = Win32.GetCurrentThreadId();
            s_hInstance = Win32.GetModuleHandleW(null);

            // Register the window class.
            fixed (char* classNamePtr = ClassName)
            {
                var wcex = new WNDCLASSEXW
                {
                    CbSize = (uint)sizeof(WNDCLASSEXW),
                    Style = 0,
                    LpfnWndProc = &WndProc,
                    CbClsExtra = 0,
                    CbWndExtra = 0,
                    HInstance = s_hInstance,
                    HIcon = IntPtr.Zero,
                    HCursor = IntPtr.Zero,
                    HbrBackground = IntPtr.Zero,
                    LpszMenuName = null,
                    LpszClassName = classNamePtr,
                    HIconSm = IntPtr.Zero,
                };

                s_classAtom = Win32.RegisterClassExW(in wcex);
                if (s_classAtom == 0)
                {
                    throw new InvalidOperationException(
                        $"RegisterClassExW failed: Win32 error 0x{Marshal.GetLastPInvokeError():X}");
                }
            }

            // Create the hidden top-level window.
            // Use WS_OVERLAPPED (style 0), 0x0 size, never ShowWindow'd. Parent = HWND_DESKTOP (IntPtr.Zero).
            s_hwnd = Win32.CreateWindowExW(
                dwExStyle: 0,
                lpClassName: ClassName,
                lpWindowName: "SpawnSpotter",
                dwStyle: Win32Const.WS_OVERLAPPED,
                X: 0, Y: 0, nWidth: 0, nHeight: 0,
                hWndParent: IntPtr.Zero,
                hMenu: IntPtr.Zero,
                hInstance: s_hInstance,
                lpParam: IntPtr.Zero);

            if (s_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateWindowExW failed: Win32 error 0x{Marshal.GetLastPInvokeError():X}");
            }

            // Install hooks ON THE STA THREAD before the message loop starts.
            // Hooks MUST be installed on the thread that pumps their messages — otherwise the
            // callbacks never fire and Windows applies LowLevelHooksTimeout (default 300 ms)
            // to every input event waiting for our unresponsive hook = sluggish mouse.
            s_onReady?.Invoke();

            // Signal the caller (Start) that we're fully ready — both window and hooks live.
            s_ready.Set();

            // Pump messages. WM_QUIT (posted by Stop or PostQuitMessage) returns false.
            while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
            {
                Win32.TranslateMessage(in msg);
                Win32.DispatchMessageW(in msg);
            }
        }
        catch (Exception ex)
        {
            s_startupError = ex;
            s_ready.Set();
        }
        finally
        {
            // Uninstall hooks (best-effort) ON THE STA THREAD — same thread that installed them.
            try { s_onTeardown?.Invoke(); } catch { }

            if (s_hwnd != IntPtr.Zero)
            {
                Win32.DestroyWindow(s_hwnd);
                s_hwnd = IntPtr.Zero;
            }
            if (s_classAtom != 0)
            {
                Win32.UnregisterClassW(ClassName, s_hInstance);
                s_classAtom = 0;
            }
        }
    }

    /// <summary>
    /// Hidden window's WndProc. Handles <c>WM_DISPLAYCHANGE</c> and <c>WM_DPICHANGED</c>
    /// to drive the monitor-topology suppression window; everything else falls through
    /// to <see cref="Win32.DefWindowProcW"/>.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Const.WM_DISPLAYCHANGE:
            case Win32Const.WM_DPICHANGED:
                // Plan section 5.5 step 2: suppress for 5 seconds after a monitor topology change.
                Volatile.Write(ref MonitorSuppressUntilTickMs, Environment.TickCount64 + 5000);
                return IntPtr.Zero;
            case Win32Const.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }
}
