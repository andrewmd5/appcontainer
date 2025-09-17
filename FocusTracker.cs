using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

public enum FocusState
{
    HasFocus,
    LostFocus,
}

public static class FocusTracker
{
    private static UnhookWinEventSafeHandle? _unhookWinHandle;
    private static unsafe delegate* unmanaged[Stdcall]<
        HWINEVENTHOOK,
        uint,
        HWND,
        int,
        int,
        uint,
        uint,
        void> _winEventProcDelegate;
    private static Action<FocusState>? _handler;
    private static HWND _hostWindow;
    private static HWND _embeddedWindow;
    private static FocusState _lastState = FocusState.LostFocus;

    /// <summary>
    /// Subscribe to focus change events for the specified host window
    /// </summary>
    /// <param name="hostWindow">The window handle to track focus for</param>
    /// <param name="handler">Callback to invoke when focus state changes</param>
    internal static void Subscribe(HWND hostWindow, Action<FocusState> handler)
    {
        _hostWindow = hostWindow;
        _handler = handler;

        unsafe
        {
            _winEventProcDelegate = &WinEventProc;
            _unhookWinHandle = PInvoke.SetWinEventHook(
                PInvoke.EVENT_SYSTEM_FOREGROUND,
                PInvoke.EVENT_SYSTEM_FOREGROUND,
                null,
                _winEventProcDelegate,
                0,
                0,
                PInvoke.WINEVENT_OUTOFCONTEXT
            );
        }

        // Check initial focus state
        CheckCurrentFocus();
    }

    /// <summary>
    /// Set the embedded window handle so we can track focus to it as well
    /// </summary>
    internal static void SetEmbeddedWindow(HWND embeddedWindow)
    {
        _embeddedWindow = embeddedWindow;
    }

    /// <summary>
    /// Unsubscribe from focus events
    /// </summary>
    public static void Unsubscribe()
    {
        if (_unhookWinHandle != null)
        {
            _unhookWinHandle.Dispose();
            _unhookWinHandle = null;
        }
        _handler = null;
    }

    /// <summary>
    /// Check if a window belongs to our container (either the host or embedded window)
    /// </summary>
    private static bool IsOurWindow(HWND hwnd)
    {
        if (hwnd == _hostWindow || hwnd == _embeddedWindow)
            return true;

        // Also check if it's a child of our host window
        var parent = PInvoke.GetParent(hwnd);
        while (parent != HWND.Null)
        {
            if (parent == _hostWindow)
                return true;
            parent = PInvoke.GetParent(parent);
        }

        return false;
    }

    /// <summary>
    /// Check the current focus state and notify if changed
    /// </summary>
    private static void CheckCurrentFocus()
    {
        if (_handler == null || _hostWindow == HWND.Null)
            return;

        var foregroundWindow = PInvoke.GetForegroundWindow();
        var newState = IsOurWindow(foregroundWindow) ? FocusState.HasFocus : FocusState.LostFocus;

        if (newState != _lastState)
        {
            _lastState = newState;
            _handler(newState);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void WinEventProc(
        HWINEVENTHOOK hWinEventHook,
        uint eventType,
        HWND hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime
    )
    {
        if (_handler == null || _hostWindow == HWND.Null)
        {
            return;
        }

        // Check if the newly focused window belongs to our container
        FocusState newState = IsOurWindow(hwnd) ? FocusState.HasFocus : FocusState.LostFocus;

        // Only notify if the state actually changed
        if (newState != _lastState)
        {
            _lastState = newState;
            _handler(newState);
        }
    }
}
