using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpawnSpotter.Native;

/// <summary>
/// All <c>[LibraryImport]</c> P/Invoke declarations used by SpawnSpotter.
/// AOT rule: every native call goes through source-generated marshaling here.
/// All blittable thanks to <c>[DisableRuntimeMarshalling]</c> at the assembly level - see
/// <see cref="BOOL"/> for the bool surrogate.
/// </summary>
internal static partial class Win32
{
    // =========================================================================
    // Message loop / window plumbing
    // =========================================================================

    // MSDN: returns int - > 0 (got a message), 0 (WM_QUIT received), -1 (error).
    // The -1 path forces callers to distinguish "stop pumping" from "try again" - a raw
    // BOOL surrogate would silently map -1 to true and re-enter the call, the canonical
    // MSDN footgun. With our args (hWnd=NULL, valid lpMsg, min=max=0) -1 is not reachable,
    // but the signature stays honest so a future caller can't trip the trap either.
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage", SetLastError = false)]
    public static partial BOOL TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW", SetLastError = false)]
    public static partial IntPtr DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage", SetLastError = false)]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    public static partial BOOL PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", SetLastError = false)]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial BOOL UnregisterClassW(string lpClassName, IntPtr hInstance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    public static partial BOOL DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = false)]
    public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow", SetLastError = false)]
    public static partial BOOL IsWindow(IntPtr hWnd);

    // =========================================================================
    // Foreground / window inspection
    // =========================================================================

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow", SetLastError = false)]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = false)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    public static unsafe partial int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    public static unsafe partial int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    public static partial int GetWindowTextLengthW(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = false)]
    public static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindow", SetLastError = false)]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const int GWL_STYLE = -16;

    /// <summary>
    /// Real-time physical key state. High bit (0x8000) of the return is set while the key is
    /// physically down. Used in the WinEvent callbacks to detect a held Win/Alt at the exact
    /// moment the foreground changed - authoritative (immune to a missed key-up desyncing our
    /// own modifier latches).
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetAsyncKeyState", SetLastError = false)]
    public static partial short GetAsyncKeyState(int vKey);

    // =========================================================================
    // WinEvent hook
    // =========================================================================

    [LibraryImport("user32.dll", EntryPoint = "SetWinEventHook", SetLastError = false)]
    public static unsafe partial IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void> pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "UnhookWinEvent", SetLastError = false)]
    public static partial BOOL UnhookWinEvent(IntPtr hWinEventHook);

    // =========================================================================
    // Low-level hooks (keyboard / mouse)
    // =========================================================================

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static unsafe partial IntPtr SetWindowsHookExW(
        int idHook,
        delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr> lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [LibraryImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
    public static partial BOOL UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll", EntryPoint = "CallNextHookEx", SetLastError = false)]
    public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // =========================================================================
    // Process / NT API
    // =========================================================================

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint dwDesiredAccess, BOOL bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    public static partial BOOL CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    public static unsafe partial BOOL QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetApplicationUserModelId", SetLastError = false)]
    public static unsafe partial int GetApplicationUserModelId(IntPtr hProcess, ref uint AppModelIDLength, char* AppModelID);

    [LibraryImport("kernel32.dll", EntryPoint = "IsWow64Process2", SetLastError = true)]
    public static partial BOOL IsWow64Process2(IntPtr hProcess, out ushort pProcessMachine, out ushort pNativeMachine);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    public static partial BOOL ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess", SetLastError = false)]
    public static partial int NtQueryInformationProcess(IntPtr ProcessHandle, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationLength, out uint ReturnLength);

    // =========================================================================
    // Privilege check
    // =========================================================================

    /// <summary>
    /// Returns TRUE when the current thread token is a member of the local Administrators
    /// group AND the token is elevated (i.e., UAC has granted us admin rights for this
    /// process). Returns FALSE for split-token standard processes even if the user is an
    /// admin. Defensive belt-and-braces check used at startup - the app.manifest already
    /// requests requireAdministrator, so the OS-level UAC prompt happens before we run.
    /// </summary>
    [LibraryImport("shell32.dll", EntryPoint = "IsUserAnAdmin", SetLastError = false)]
    public static partial BOOL IsUserAnAdmin();

    // =========================================================================
    // Helpers
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ReadString(ReadOnlySpan<char> buffer)
        => buffer.IsEmpty ? string.Empty : new string(buffer);
}
