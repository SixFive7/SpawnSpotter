using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpawnSpotter.Native;

namespace SpawnSpotter.Hooks;

/// <summary>
/// An STA thread that hosts a Windows message pump and installs/uninstalls exactly one hook
/// via the provided <c>onReady</c> / <c>onTeardown</c> callbacks. Designed to give every hook
/// its own thread, so a slow callback in one hook can never queue input events from another hook.
///
/// <para>
/// Per Microsoft Docs, low-level hooks (<c>WH_KEYBOARD_LL</c>, <c>WH_MOUSE_LL</c>) and
/// <c>SetWinEventHook</c> with <c>WINEVENT_OUTOFCONTEXT</c> all require the installing thread
/// to have an active message loop — that's what dispatches their callbacks. The thread that
/// installs the hook is the thread its callbacks fire on. Sharing one thread across hooks means
/// a callback in flight can delay every other hook's callbacks behind it on the same queue.
/// One thread per hook eliminates that cross-hook interference entirely.
/// </para>
///
/// <para>
/// At most one instance is given <see cref="withWindow"/>=true. That instance also creates a
/// hidden top-level window and dispatches <c>WM_DISPLAYCHANGE</c> / <c>WM_DPICHANGED</c>
/// through <see cref="WndProc"/> to update <see cref="MonitorSuppressUntilTickMs"/>.
/// (Display-change broadcasts go to top-level windows; the WinEvent foreground host is a natural
/// place to put it since both relate to "window-state changes.")
/// </para>
/// </summary>
internal sealed class HookHostThread
{
    private readonly string _name;
    private readonly Action _onReady;
    private readonly Action _onTeardown;
    private readonly bool _withWindow;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private IntPtr _hInstance;
    private ushort _classAtom;
    private string _className = string.Empty;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startupError;

    /// <summary>
    /// Read by the classifier; written by <see cref="WndProc"/> when the OS broadcasts
    /// <c>WM_DISPLAYCHANGE</c> / <c>WM_DPICHANGED</c>. For 5 seconds after either, foreground-change
    /// events are classified as <c>USER_OTHER</c> rather than <c>STEAL</c>. The static field lives
    /// here because only the host instance that owns the hidden window updates it; all other hook
    /// hosts (and the classifier) just read.
    /// </summary>
    public static long MonitorSuppressUntilTickMs;

    public HookHostThread(string name, Action onReady, Action onTeardown, bool withWindow = false)
    {
        _name = name;
        _onReady = onReady;
        _onTeardown = onTeardown;
        _withWindow = withWindow;
    }

    public uint ThreadId => _threadId;
    public IntPtr Hwnd => _hwnd;

    /// <summary>
    /// Spins up the STA thread, registers a window class (if <c>withWindow</c>), creates the hidden
    /// window, then invokes <c>onReady</c> ON THE STA THREAD (so the hook is owned by the pumping
    /// thread). Returns once the hook is installed; throws if any step fails.
    /// </summary>
    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException($"{_name} HookHostThread already started.");
        }

        _thread = new Thread(ThreadEntry)
        {
            Name = $"SpawnSpotter.{_name}",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _ready.Wait();
        if (_startupError is not null)
        {
            throw new InvalidOperationException($"{_name} HookHostThread startup failed.", _startupError);
        }
    }

    /// <summary>
    /// Posts <c>WM_QUIT</c> to the STA thread, which exits the message loop, then invokes
    /// <c>onTeardown</c> (uninstall hook) and destroys the hidden window. Joins the thread.
    /// Safe to call multiple times.
    /// </summary>
    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        Win32.PostThreadMessageW(_threadId, Win32Const.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join();
        _thread = null;
    }

    private void ThreadEntry()
    {
        try
        {
            _threadId = Win32.GetCurrentThreadId();
            _hInstance = Win32.GetModuleHandleW(null);

            if (_withWindow)
            {
                CreateMonitorWindow();
            }

            // Install the hook ON the STA thread — this is the point of the whole class.
            _onReady();

            _ready.Set();

            while (true)
            {
                var rc = Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                if (rc == 0) { break; } // WM_QUIT
                if (rc < 0)
                {
                    // Per MSDN: -1 indicates an error. Not reachable with our args (hWnd=NULL,
                    // valid lpMsg, min=max=0), but treat defensively. Hard-fail per project policy.
                    throw new InvalidOperationException(
                        $"GetMessageW returned -1 (Win32 error 0x{Marshal.GetLastPInvokeError():X}).");
                }
                Win32.TranslateMessage(in msg);
                Win32.DispatchMessageW(in msg);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
        finally
        {
            // Uninstall the hook on the same STA thread that installed it.
            try { _onTeardown(); } catch { }

            if (_hwnd != IntPtr.Zero)
            {
                Win32.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_classAtom != 0)
            {
                Win32.UnregisterClassW(_className, _hInstance);
                _classAtom = 0;
            }
        }
    }

    private unsafe void CreateMonitorWindow()
    {
        _className = string.Create(CultureInfo.InvariantCulture,
            $"SpawnSpotter.{_name}Window.{Environment.ProcessId}");

        fixed (char* classNamePtr = _className)
        {
            var wcex = new WNDCLASSEXW
            {
                CbSize = (uint)sizeof(WNDCLASSEXW),
                Style = 0,
                LpfnWndProc = &WndProc,
                CbClsExtra = 0,
                CbWndExtra = 0,
                HInstance = _hInstance,
                HIcon = IntPtr.Zero,
                HCursor = IntPtr.Zero,
                HbrBackground = IntPtr.Zero,
                LpszMenuName = null,
                LpszClassName = classNamePtr,
                HIconSm = IntPtr.Zero,
            };

            _classAtom = Win32.RegisterClassExW(in wcex);
            if (_classAtom == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassExW failed: Win32 error 0x{Marshal.GetLastPInvokeError():X}");
            }
        }

        _hwnd = Win32.CreateWindowExW(
            dwExStyle: 0,
            lpClassName: _className,
            lpWindowName: "SpawnSpotter",
            dwStyle: Win32Const.WS_OVERLAPPED,
            X: 0, Y: 0, nWidth: 0, nHeight: 0,
            hWndParent: IntPtr.Zero,
            hMenu: IntPtr.Zero,
            hInstance: _hInstance,
            lpParam: IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed: Win32 error 0x{Marshal.GetLastPInvokeError():X}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Const.WM_DISPLAYCHANGE:
            case Win32Const.WM_DPICHANGED:
                // Suppress for 5 seconds after a monitor topology change.
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
