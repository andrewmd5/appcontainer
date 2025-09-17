using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Magnification;

namespace AppContainer
{
    internal partial class Program
    {
        private delegate LRESULT WndProcDelegate(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam);
        private static readonly WndProcDelegate _wndProcDelegate = WindowProc;
        private static Process? appProcess;
        private static HWND hostWindow;
        private static HWND magnifierHostWindow;
        private static Bitmap? backgroundImage;
        private static Bitmap? overlayImage;
        private static string? overlayPosition;
        private static HWND appWindow;
        private static int appWidth;
        private static int appHeight;
        private static int appX = -1;
        private static int appY = -1;
        private static bool useCustomPosition = false;
        private static Monitor currentMonitor;
        private static readonly string logFilePath = "AppContainer.log";

        private static Windows.Win32.UI.HiDpi.DPI_AWARENESS appDpiAwareness;


        private static float zoomFactor = 1.0f;
        private static bool magnificationEnabled = false;
        private static readonly float[] zoomLevels =
        [
            1.0f,
            1.1f,
            1.25f,
            1.5f,
            1.75f,
            2.0f,
            2.5f,
            3.0f,
            4.0f,
        ];
        private static int currentZoomIndex = 0;
        private static HWND magnifierWindow = HWND.Null;
        private const string MAGNIFIER_WINDOW_CLASS = "Magnifier";
        private const string MAGNIFIER_HOST_CLASS = "MagnifierHostClass";

        private delegate void WindowSizeChanged();
        private static event WindowSizeChanged? OnWindowSizeChanged;


        [DllImport("Magnification.dll")]
        private static extern bool MagSetFullscreenUseBitmapSmoothing(bool useSmoothing);

        [DllImport("Magnification.dll")]
        private static extern bool MagSetLensUseBitmapSmoothing(bool useSmoothing);

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {

                var arguments = Utils.ParseArguments(args);
#if !DEBUG
                PInvoke.AttachConsole(unchecked((uint)-1));
#endif

                Log("Application started (DPI-unaware mode)");


                if (arguments.TryGetValue("window-title", out var appWindowTitle))
                {
                    appWindow = PInvoke.FindWindow(null, appWindowTitle);
                    if (appWindow == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            $"Could not find a window with the title: '{appWindowTitle}'"
                        );
                    }
                }
                else if (arguments.TryGetValue("window-handle", out var windowHandle))
                {
                    appWindow = Utils.ConvertAndValidateWindowHandle(windowHandle);
                }
                else
                {
                    throw new ArgumentException(
                        "Neither 'window-title' nor 'window-handle' argument provided"
                    );
                }

                appProcess = GetProcessByWindow(appWindow);
                if (appProcess is null)
                {
                    throw new InvalidOperationException("Failed to get app process");
                }

                currentMonitor = Utils.GetMonitorFromWindow(appWindow);


                LoadBackground(arguments, currentMonitor);


                LoadOverlay(arguments);


                ParseWindowGeometry(arguments, currentMonitor);

                InitializeMagnification();


                hostWindow = CreateHostWindow(currentMonitor);
                if (hostWindow == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create host window");
                }
                if (appProcess != null)
                {
                    PInvoke.SetWindowLong(
                        hostWindow,
                        Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWLP_USERDATA,
                        appProcess.Id
                    );
                }
                Log("Host window created successfully");

                UpdateHostWindowTitleAndIcon();
                EmbedAppWindow();


                if (magnificationEnabled)
                {
                    CreateMagnifierHostWindow();
                    CreateMagnifierWindow();
                    RegisterZoomHotkeys();


                    unsafe
                    {
                        PInvoke.SetTimer(hostWindow, 2, 16, null);
                    }
                }


                appProcess.EnableRaisingEvents = true;
                appProcess.Exited += (sender, e) =>
                {
                    Log("App process exited");
                    PInvoke.PostMessage(hostWindow, PInvoke.WM_CLOSE, 0, IntPtr.Zero);
                };

                OnWindowSizeChanged += HandleWindowSizeChanged;


                FocusTracker.Subscribe(hostWindow, HandleFocusChange);
                FocusTracker.SetEmbeddedWindow(appWindow);

                RunMessageLoop();
            }
            catch (Exception ex)
            {
                Log($"Fatal error: {ex.Message}\n{ex.StackTrace}");
                string errorMessage =
                    $"An error occurred: {ex.Message}\n\nCheck log: {logFilePath}";
                PInvoke.MessageBox(
                    HWND.Null,
                    errorMessage,
                    "Error",
                    Windows.Win32.UI.WindowsAndMessaging.MESSAGEBOX_STYLE.MB_OK
                        | Windows.Win32.UI.WindowsAndMessaging.MESSAGEBOX_STYLE.MB_ICONERROR
                );
            }
            finally
            {
                FocusTracker.Unsubscribe();
                if (magnificationEnabled)
                {
                    UninitializeMagnification();
                }
                backgroundImage?.Dispose();
                overlayImage?.Dispose();
                Log("Application ended");

#if !DEBUG
                PInvoke.FreeConsole();
#endif
                Environment.Exit(0);
            }
        }

        private static void ParseWindowGeometry(
            ImmutableDictionary<string, string> arguments,
            Monitor monitor
        )
        {

            if (
                !arguments.TryGetValue("width", out var width)
                || !arguments.TryGetValue("height", out var height)
            )
            {
                throw new ArgumentException("Width and height arguments are required");
            }

            if (!int.TryParse(width, out appWidth) || !int.TryParse(height, out appHeight))
            {
                throw new ArgumentException("Invalid width or height value");
            }

            if (appWidth < -1 || appHeight < -1)
            {
                throw new ArgumentOutOfRangeException(
                    "width/height",
                    "Values must be -1, 0, or positive"
                );
            }


            if (appProcess != null)
            {
                var dpiContext = PInvoke.GetDpiAwarenessContextForProcess(appProcess.SafeHandle);
                appDpiAwareness = PInvoke.GetAwarenessFromDpiAwarenessContext(dpiContext);

                Log($"Process DPI Awareness: {appDpiAwareness}");


                uint windowDpi = PInvoke.GetDpiForWindow(appWindow);
                float dpiScale = windowDpi / 96.0f;
                Log($"Window DPI: {windowDpi} (scale: {dpiScale:F2})");
            }
            else
            {

                appDpiAwareness = Windows.Win32.UI.HiDpi.DPI_AWARENESS.DPI_AWARENESS_UNAWARE;
                Log(
                    $"Warning: Could not get process handle for DPI awareness check, assuming DPI_UNAWARE"
                );
            }


            if (
                arguments.TryGetValue("x", out var xStr) && arguments.TryGetValue("y", out var yStr)
            )
            {
                if (int.TryParse(xStr, out appX) && int.TryParse(yStr, out appY))
                {
                    useCustomPosition = true;
                    Log(
                        $"Original coordinates from BorderlessGaming: X={appX}, Y={appY}, W={appWidth}, H={appHeight}"
                    );



                    if (
                        appDpiAwareness
                        != Windows.Win32.UI.HiDpi.DPI_AWARENESS.DPI_AWARENESS_UNAWARE
                    )
                    {
                        uint windowDpi = PInvoke.GetDpiForWindow(appWindow);
                        float dpiScale = windowDpi / 96.0f;


                        appX = (int)(appX / dpiScale);
                        appY = (int)(appY / dpiScale);
                        appWidth = (int)(appWidth / dpiScale);
                        appHeight = (int)(appHeight / dpiScale);

                        Log(
                            $"DPI-aware process detected ({appDpiAwareness}), scaled coordinates: X={appX}, Y={appY}, W={appWidth}, H={appHeight}"
                        );
                    }
                    else
                    {
                        Log($"DPI-unaware process detected, using coordinates as-is");
                    }

                    Log(
                        $"Monitor info: X={monitor.X}, Y={monitor.Y}, Width={monitor.Width}, Height={monitor.Height}"
                    );
                }
            }
        }




        private static void HandleFocusChange(FocusState state)
        {
            if (state == FocusState.HasFocus)
            {
                Log("Host window gained focus");


                if (magnificationEnabled && zoomFactor > 1.0f && magnifierHostWindow != HWND.Null)
                {
                    PInvoke.ShowWindow(
                        magnifierHostWindow,
                        Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE
                    );
                    PInvoke.MagShowSystemCursor(false);
                }


                if (appWindow != HWND.Null)
                {
                    PInvoke.EnableWindow(appWindow, true);
                    PInvoke.SetForegroundWindow(appWindow);
                    PInvoke.SetFocus(appWindow);
                }
            }
            else
            {
                Log("Host window lost focus");


                if (magnificationEnabled && magnifierHostWindow != HWND.Null)
                {
                    PInvoke.ShowWindow(
                        magnifierHostWindow,
                        Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_HIDE
                    );
                    PInvoke.MagShowSystemCursor(true);
                }
            }
        }

        private static void LoadBackground(
            ImmutableDictionary<string, string> arguments,
            Monitor monitor
        )
        {
            if (arguments.TryGetValue("background-image", out string? backgroundImagePath))
            {
                if (!File.Exists(backgroundImagePath))
                {
                    throw new FileNotFoundException(
                        $"Background image not found: {backgroundImagePath}"
                    );
                }
                backgroundImage = new Bitmap(backgroundImagePath);
                Log($"Background image loaded: {backgroundImagePath}");
            }
            else if (arguments.TryGetValue("background-color", out string? backgroundColorHex))
            {
                if (!Utils.IsValidHexColor(backgroundColorHex))
                {
                    throw new ArgumentException("Invalid background color hex value");
                }
                Color backgroundColor = ColorTranslator.FromHtml(backgroundColorHex);
                backgroundImage = Utils.CreateSolidColorBitmap(
                    backgroundColor,
                    monitor.Width,
                    monitor.Height
                );
                Log($"Background color set: {backgroundColorHex}");
            }
            else if (arguments.TryGetValue("background-gradient", out string? gradientColors))
            {
                var colors = gradientColors.Split(';');
                if (
                    colors.Length != 2
                    || !Utils.IsValidHexColor(colors[0])
                    || !Utils.IsValidHexColor(colors[1])
                )
                {
                    throw new ArgumentException("Invalid gradient format");
                }
                Color color1 = ColorTranslator.FromHtml(colors[0]);
                Color color2 = ColorTranslator.FromHtml(colors[1]);
                backgroundImage = Utils.CreateGradientBitmap(
                    color1,
                    color2,
                    monitor.Width,
                    monitor.Height
                );
                Log($"Background gradient set");
            }
            else
            {
                throw new ArgumentException("Background image or color required");
            }
        }

        private static void LoadOverlay(ImmutableDictionary<string, string> arguments)
        {
            if (arguments.TryGetValue("overlay-image", out string? overlayImagePath))
            {
                if (!File.Exists(overlayImagePath))
                {
                    Log($"Warning: Overlay image not found: {overlayImagePath}");
                    return;
                }

                overlayImage = new Bitmap(overlayImagePath);

                if (arguments.TryGetValue("overlay-position", out string? position))
                {
                    overlayPosition = position.ToLower();
                    if (!IsValidOverlayPosition(overlayPosition))
                    {
                        overlayImage.Dispose();
                        overlayImage = null;
                        throw new ArgumentException("Invalid overlay position");
                    }
                    Log($"Overlay loaded at position: {overlayPosition}");
                }
                else
                {
                    overlayImage.Dispose();
                    overlayImage = null;
                    throw new ArgumentException("overlay-position required with overlay-image");
                }
            }
        }

        private static void InitializeMagnification()
        {
            try
            {
                if (PInvoke.MagInitialize())
                {
                    magnificationEnabled = true;
                    MagSetFullscreenUseBitmapSmoothing(true);
                    MagSetLensUseBitmapSmoothing(true);
                    Log("Magnification API initialized");
                }
                else
                {
                    Log("Warning: Magnification API failed to initialize");
                }
            }
            catch (Exception ex)
            {
                Log($"Error initializing Magnification API: {ex.Message}");
            }
        }

        private static unsafe void CreateMagnifierHostWindow()
        {
            if (!magnificationEnabled)
                return;

            try
            {
                HINSTANCE hInstance = new(Process.GetCurrentProcess().Handle);
                var safeHandler = Process.GetCurrentProcess().SafeHandle;

                ushort classId;
                fixed (char* pClassName = MAGNIFIER_HOST_CLASS)
                {
                    Windows.Win32.UI.WindowsAndMessaging.WNDCLASSEXW wndClass = new()
                    {
                        cbSize = (uint)
                            Marshal.SizeOf<Windows.Win32.UI.WindowsAndMessaging.WNDCLASSEXW>(),
                        lpfnWndProc = (delegate* unmanaged[Stdcall]<
                            HWND,
                            uint,
                            WPARAM,
                            LPARAM,
                            LRESULT>)
                            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                        hInstance = hInstance,
                        lpszClassName = pClassName,
                        hbrBackground = new HBRUSH((IntPtr)(1 + COLOR_BTNFACE)),
                    };

                    classId = PInvoke.RegisterClassEx(in wndClass);
                    if (classId == 0)
                    {
                        Log($"Warning: Failed to register magnifier host class");
                        return;
                    }
                }

                if (!PInvoke.GetWindowRect(hostWindow, out var hostRect))
                    return;

                fixed (char* pClassName = MAGNIFIER_HOST_CLASS)
                fixed (char* pWindowName = "MagnifierHost")
                {
                    magnifierHostWindow = PInvoke.CreateWindowEx(
                        Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOPMOST
                            | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_LAYERED
                            | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                            | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TRANSPARENT
                            | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                        MAGNIFIER_HOST_CLASS,
                        "MagnifierHost",
                        Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP
                            | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_CLIPCHILDREN,
                        hostRect.left,
                        hostRect.top,
                        hostRect.right - hostRect.left,
                        hostRect.bottom - hostRect.top,
                        HWND.Null,
                        null,
                        safeHandler,
                        null
                    );
                }

                if (magnifierHostWindow == IntPtr.Zero)
                {
                    Log($"Warning: Failed to create magnifier host window");
                    return;
                }
                PInvoke.SetLayeredWindowAttributes(
                    magnifierHostWindow,
                    default,
                    255,
                    Windows.Win32.UI.WindowsAndMessaging.LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA
                );
                if (magnifierHostWindow != HWND.Null && appProcess != null)
                {
                    PInvoke.SetWindowLong(
                        magnifierHostWindow,
                        Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWLP_USERDATA,
                       appProcess.Id
                    );
                }

                PInvoke.ShowWindow(
                    magnifierHostWindow,
                    Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_HIDE
                );

                Log("Magnifier host window created");
            }
            catch (Exception ex)
            {
                Log($"Error creating magnifier host: {ex.Message}");
            }
        }

        private static unsafe void CreateMagnifierWindow()
        {
            if (!magnificationEnabled || magnifierHostWindow == HWND.Null)
                return;

            try
            {
                if (!PInvoke.GetClientRect(magnifierHostWindow, out var clientRect))
                    return;

                int width = clientRect.right - clientRect.left;
                int height = clientRect.bottom - clientRect.top;

                magnifierWindow = PInvoke.CreateWindowEx(
                    0,
                    MAGNIFIER_WINDOW_CLASS,
                    "Magnifier",
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_CHILD
                        | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_VISIBLE
                        | (Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE)MS_SHOWMAGNIFIEDCURSOR,
                    0,
                    0,
                    width,
                    height,
                    magnifierHostWindow,
                    null,
                    null,
                    null
                );

                if (magnifierWindow == IntPtr.Zero)
                {
                    Log($"Warning: Failed to create magnifier window");
                    return;
                }

                SetMagnifierTransform(1.0f);
                Log("Magnifier window created");
            }
            catch (Exception ex)
            {
                Log($"Error creating magnifier: {ex.Message}");
            }
        }

        private static void UpdateMagnifierSource()
        {
            if (magnifierWindow == HWND.Null || !magnificationEnabled)
                return;

            if (PInvoke.GetWindowRect(appWindow, out var appRect))
            {
                RECT sourceRect = new()
                {
                    left = appRect.left,
                    top = appRect.top,
                    right = appRect.right,
                    bottom = appRect.bottom,
                };

                PInvoke.MagSetWindowSource(magnifierWindow, sourceRect);
                PInvoke.InvalidateRect(magnifierWindow, (RECT?)null, false);
            }
        }

        private static void SetMagnifierTransform(float magnificationFactor)
        {
            if (magnifierWindow == HWND.Null || !magnificationEnabled)
                return;

            try
            {
                MAGTRANSFORM transform = new();
                transform.v[0] = magnificationFactor;
                transform.v[4] = magnificationFactor;
                transform.v[8] = 1;

                if (PInvoke.MagSetWindowTransform(magnifierWindow, ref transform))
                {
                    Log($"Magnifier transform set to {magnificationFactor:F2}x");
                }
            }
            catch (Exception ex)
            {
                Log($"Error setting transform: {ex.Message}");
            }
        }

        private static bool SetZoom(float magnificationFactor)
        {
            if (magnificationFactor < 1.0f || magnifierWindow == HWND.Null)
                return false;

            zoomFactor = magnificationFactor;

            if (Math.Abs(zoomFactor - 1.0f) < 0.01f)
            {
                PInvoke.ShowWindow(
                    magnifierHostWindow,
                    Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_HIDE
                );
                PInvoke.MagShowSystemCursor(true);
            }
            else
            {
                SetMagnifierTransform(zoomFactor);
                UpdateMagnifierSource();
                ResizeMagnifierHostWindow();


                if (PInvoke.GetForegroundWindow() == appWindow)
                {
                    PInvoke.ShowWindow(
                        magnifierHostWindow,
                        Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE
                    );
                    PInvoke.MagShowSystemCursor(false);
                }
            }

            Log($"Zoom level: {magnificationFactor:F2}x");
            return true;
        }

        private static void ResizeMagnifierHostWindow()
        {
            if (magnifierHostWindow == HWND.Null || !magnificationEnabled)
                return;

            if (!PInvoke.GetWindowRect(hostWindow, out var hostRect))
                return;

            int hostWidth = hostRect.right - hostRect.left;
            int hostHeight = hostRect.bottom - hostRect.top;

            int magWidth = (int)(appWidth * zoomFactor);
            int magHeight = (int)(appHeight * zoomFactor);


            magWidth = Math.Min(magWidth, hostWidth);
            magHeight = Math.Min(magHeight, hostHeight);

            int x = hostRect.left + (hostWidth - magWidth) / 2;
            int y = hostRect.top + (hostHeight - magHeight) / 2;

            PInvoke.SetWindowPos(
                magnifierHostWindow,
                HWND.Null,
                x,
                y,
                magWidth,
                magHeight,
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER
                    | Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
            );

            if (magnifierWindow != HWND.Null)
            {
                PInvoke.MoveWindow(magnifierWindow, 0, 0, magWidth, magHeight, true);
            }
        }

        private static void UninitializeMagnification()
        {
            if (!magnificationEnabled)
                return;

            UnregisterZoomHotkeys();

            if (magnifierWindow != HWND.Null)
            {
                PInvoke.DestroyWindow(magnifierWindow);
                magnifierWindow = HWND.Null;
            }

            if (magnifierHostWindow != HWND.Null)
            {
                PInvoke.DestroyWindow(magnifierHostWindow);
                magnifierHostWindow = HWND.Null;
            }

            PInvoke.MagUninitialize();
            magnificationEnabled = false;
            Log("Magnification uninitialized");
        }

        private static void RegisterZoomHotkeys()
        {
            TryRegisterHotkey(
                HOTKEY_ZOOM_IN,
                Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS.MOD_CONTROL
                    | HOT_KEY_MODIFIERS.MOD_SHIFT,
                (uint)VIRTUAL_KEY.VK_OEM_PLUS,
                "Zoom In"
            );

            TryRegisterHotkey(
                HOTKEY_ZOOM_OUT,
                Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS.MOD_CONTROL
                    | HOT_KEY_MODIFIERS.MOD_SHIFT,
                (uint)VIRTUAL_KEY.VK_OEM_MINUS,
                "Zoom Out"
            );

            TryRegisterHotkey(
                HOTKEY_ZOOM_RESET,
                Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS.MOD_CONTROL
                    | HOT_KEY_MODIFIERS.MOD_SHIFT,
                (uint)VIRTUAL_KEY.VK_0,
                "Zoom Reset"
            );

            Log("Zoom hotkeys registered");
        }

        private static void TryRegisterHotkey(
            int id,
            HOT_KEY_MODIFIERS mod,
            uint vk,
            string hotkeyName
        )
        {
            var keyCombo = $"{mod}+{(VIRTUAL_KEY)vk}";
            Log(
                $"Attempting to register hotkey '{hotkeyName}' (ID {id}) with combination {keyCombo}"
            );

            if (!PInvoke.RegisterHotKey(hostWindow, id, mod, vk))
            {
                var error = Marshal.GetLastPInvokeError();
                var message = Marshal.GetLastPInvokeErrorMessage();
                Log(
                    $"Failed to register hotkey '{hotkeyName}' ({keyCombo}): Error {error} - {message}"
                );
                return;
            }

            Log($"Successfully registered hotkey '{hotkeyName}' ({keyCombo})");
        }

        private static void UnregisterZoomHotkeys()
        {
            PInvoke.UnregisterHotKey(hostWindow, HOTKEY_ZOOM_IN);
            PInvoke.UnregisterHotKey(hostWindow, HOTKEY_ZOOM_OUT);
            PInvoke.UnregisterHotKey(hostWindow, HOTKEY_ZOOM_RESET);
        }

        private static void ZoomIn()
        {
            if (currentZoomIndex < zoomLevels.Length - 1)
            {
                currentZoomIndex++;
                SetZoom(zoomLevels[currentZoomIndex]);
            }
        }

        private static void ZoomOut()
        {
            if (currentZoomIndex > 0)
            {
                currentZoomIndex--;
                SetZoom(zoomLevels[currentZoomIndex]);
            }
        }

        private static void ResetZoom()
        {
            currentZoomIndex = 0;
            SetZoom(1.0f);
        }

        private static unsafe HWND CreateHostWindow(Monitor monitor)
        {
            HINSTANCE hInstance = new(Process.GetCurrentProcess().Handle);

            const string WindowClassName = "AppContainerClass";
            const string WindowName = "AppContainer";

            ushort classId;
            fixed (char* pClassName = WindowClassName)
            {
                Windows.Win32.UI.WindowsAndMessaging.WNDCLASSEXW wndClass = new()
                {
                    cbSize = (uint)
                        Marshal.SizeOf<Windows.Win32.UI.WindowsAndMessaging.WNDCLASSEXW>(),
                    lpfnWndProc = (delegate* unmanaged[Stdcall]<
                        HWND,
                        uint,
                        WPARAM,
                        LPARAM,
                        LRESULT>)
                        Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    hInstance = hInstance,
                    lpszClassName = pClassName,
                    hbrBackground = HBRUSH.Null,
                };

                classId = PInvoke.RegisterClassEx(in wndClass);
                if (classId == 0)
                {
                    throw new Exception($"Failed to register window class");
                }
            }


            fixed (char* pClassName = WindowClassName)
            fixed (char* pWindowName = WindowName)
            {
                HWND hwnd = PInvoke.CreateWindowEx(
                    0,
                    pClassName,
                    pWindowName,
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP
                        | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_VISIBLE,
                    monitor.X,
                    monitor.Y,
                    monitor.Width,
                    monitor.Height,
                    HWND.Null,
                    Windows.Win32.UI.WindowsAndMessaging.HMENU.Null,
                    hInstance,
                    null
                );

                if (hwnd == IntPtr.Zero)
                {
                    throw new Exception($"Failed to create window");
                }

                return hwnd;
            }
        }

        private static async Task RemoveWindowStylesDelayed()
        {

            await Task.Delay(500);


            var currentStyle = (Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE)
                PInvoke.GetWindowLong(
                    appWindow,
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_STYLE
                );

            var currentExtendedStyle = (Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE)
                PInvoke.GetWindowLong(
                    appWindow,
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE
                );


            currentStyle &= ~(
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_CAPTION
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_THICKFRAME
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_SYSMENU
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MAXIMIZEBOX
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MINIMIZEBOX
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_DLGFRAME
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_BORDER
            );


            currentStyle |= Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_CHILD;


            currentExtendedStyle &= ~(
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_DLGMODALFRAME
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_COMPOSITED
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_WINDOWEDGE
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_CLIENTEDGE
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_LAYERED
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_STATICEDGE
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                | Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_APPWINDOW
            );


            PInvoke.SetWindowLong(
                appWindow,
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_STYLE,
                (int)currentStyle
            );

            PInvoke.SetWindowLong(
                appWindow,
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
                (int)currentExtendedStyle
            );


            PInvoke.SetWindowPos(
                appWindow,
                HWND.Null,
                0,
                0,
                0,
                0,
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                    | Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                    | Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER
                    | Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED
            );

            Log("Window styles removed after settling");
        }

        private static void EmbedAppWindow()
        {
            PInvoke.ShowWindow(
                appWindow,
                Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE
            );
            DetermineAppWindowSize();

            if (PInvoke.SetParent(appWindow, hostWindow) == HWND.Null)
            {
                throw new Exception($"Failed to set parent window");
            }


            var currentStyle = (Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE)
                PInvoke.GetWindowLong(
                    appWindow,
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_STYLE
                );

            if (
                (currentStyle & Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_CAPTION) != 0
                || (currentStyle & Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_THICKFRAME)
                    != 0
            )
            {
                var style = currentStyle;
                style &= ~(
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_CAPTION
                    | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_THICKFRAME
                    | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MINIMIZE
                    | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MAXIMIZE
                    | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_SYSMENU
                );
                style |= Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_CHILD;

                PInvoke.SetWindowLong(
                    appWindow,
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_STYLE,
                    (int)style
                );

                PInvoke.SetWindowPos(
                    appWindow,
                    HWND.Null,
                    0,
                    0,
                    0,
                    0,
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                        | Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                        | Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER
                        | Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED
                );
            }
            else
            {
                PInvoke.SetWindowLong(
                    appWindow,
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_STYLE,
                    (int)(currentStyle | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_CHILD)
                );
            }





            bool needsRepositioning = false;

            if (appDpiAwareness != Windows.Win32.UI.HiDpi.DPI_AWARENESS.DPI_AWARENESS_UNAWARE)
            {

                needsRepositioning = true;
                Log($"DPI-aware app - will reposition after embedding");
            }
            else if (!useCustomPosition)
            {

                needsRepositioning = true;
                Log($"Fullscreen/centered mode - will reposition after embedding");
            }
            else if (appWidth <= 0 || appHeight <= 0)
            {

                needsRepositioning = true;
                Log(
                    $"Special size values (width={appWidth}, height={appHeight}) - will reposition after embedding"
                );
            }
            else
            {

                Log(
                    $"DPI-unaware with custom position - skipping repositioning to preserve BorderlessGaming coordinates"
                );
            }

            if (needsRepositioning)
            {
                PositionAppWindow();
            }

            string positionLog = useCustomPosition
                ? $"App window embedded: {appWidth}x{appHeight} at position ({appX - currentMonitor.X},{appY - currentMonitor.Y}) relative to monitor"
                : $"App window embedded: {appWidth}x{appHeight} centered";
            Log(positionLog);


            Task.Run(RemoveWindowStylesDelayed);
        }

        private static void DetermineAppWindowSize()
        {
            if (appWidth == 0 && appHeight == 0)
            {


                if (PInvoke.GetWindowRect(appWindow, out var currentAppRect))
                {
                    int currentWidth = currentAppRect.right - currentAppRect.left;
                    int currentHeight = currentAppRect.bottom - currentAppRect.top;


                    if (PInvoke.GetClientRect(hostWindow, out var hostRect))
                    {
                        int hostWidth = hostRect.right - hostRect.left;
                        int hostHeight = hostRect.bottom - hostRect.top;


                        if (currentWidth < hostWidth || currentHeight < hostHeight)
                        {
                            appWidth = currentWidth;
                            appHeight = currentHeight;
                            Log(
                                $"Using app's current size (likely PreserveClientArea): {appWidth}x{appHeight}"
                            );
                        }
                        else
                        {

                            appWidth = hostWidth;
                            appHeight = hostHeight;
                            Log($"Using host's full size: {appWidth}x{appHeight}");
                        }
                    }
                }
            }
            else if (appWidth == -1 && appHeight == -1)
            {

                if (PInvoke.GetWindowRect(appWindow, out var currentRect))
                {
                    appWidth = currentRect.right - currentRect.left;
                    appHeight = currentRect.bottom - currentRect.top;
                    Log($"Using current window size: {appWidth}x{appHeight}");
                }
            }
            else if (appWidth > 0 && appHeight > 0)
            {

                Log($"Using explicit size: {appWidth}x{appHeight}");
            }
        }

        private static void HandleWindowSizeChanged()
        {
            if (!PInvoke.GetWindowRect(appWindow, out var appRect))
                return;

            int newWidth = appRect.right - appRect.left;
            int newHeight = appRect.bottom - appRect.top;

            if (newWidth != appWidth || newHeight != appHeight)
            {
                appWidth = newWidth;
                appHeight = newHeight;
                PositionAppWindow();

                if (magnificationEnabled && magnifierWindow != HWND.Null && zoomFactor > 1.0f)
                {
                    UpdateMagnifierSource();
                    ResizeMagnifierHostWindow();
                }

                PInvoke.RedrawWindow(
                    hostWindow,
                    null,
                    null,
                    Windows.Win32.Graphics.Gdi.REDRAW_WINDOW_FLAGS.RDW_INVALIDATE
                        | Windows.Win32.Graphics.Gdi.REDRAW_WINDOW_FLAGS.RDW_UPDATENOW
                );
            }
        }

        private static void PositionAppWindow()
        {
            if (useCustomPosition)
            {


                int relativeX = appX - currentMonitor.X;
                int relativeY = appY - currentMonitor.Y;

                Log(
                    $"Positioning app window: Adjusted ({appX},{appY}) -> Container relative ({relativeX},{relativeY}), Size: {appWidth}x{appHeight}"
                );
                PInvoke.MoveWindow(appWindow, relativeX, relativeY, appWidth, appHeight, true);
            }
            else
            {

                if (!PInvoke.GetClientRect(hostWindow, out var clientRect))
                    return;

                int hostWidth = clientRect.right - clientRect.left;
                int hostHeight = clientRect.bottom - clientRect.top;

                int x = (hostWidth - appWidth) / 2;
                int y = (hostHeight - appHeight) / 2;

                PInvoke.MoveWindow(appWindow, x, y, appWidth, appHeight, true);
            }
        }

        private static unsafe void UpdateHostWindowTitleAndIcon()
        {
            int bufferSize = PInvoke.GetWindowTextLength(appWindow) + 1;
            fixed (char* windowNameChars = new char[bufferSize])
            {
                if (PInvoke.GetWindowText(appWindow, windowNameChars, bufferSize) > 0)
                {
                    string appWindowTitle = new(windowNameChars);
                    PInvoke.SetWindowText(hostWindow, appWindowTitle);
                }
            }

            IntPtr hIcon = PInvoke.SendMessage(
                appWindow,
                PInvoke.WM_GETICON,
                PInvoke.ICON_BIG,
                IntPtr.Zero
            );
            if (hIcon == IntPtr.Zero)
            {
                nuint classLongPtr = PInvoke.GetClassLongPtr(
                    appWindow,
                    Windows.Win32.UI.WindowsAndMessaging.GET_CLASS_LONG_INDEX.GCL_HICON
                );
                hIcon = (IntPtr)classLongPtr;
            }
            if (hIcon != IntPtr.Zero)
            {
                PInvoke.SendMessage(hostWindow, PInvoke.WM_SETICON, PInvoke.ICON_BIG, hIcon);
            }
        }

        private static void RunMessageLoop()
        {
            Log("Entering message loop");
            while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
            {
                PInvoke.TranslateMessage(msg);
                PInvoke.DispatchMessage(msg);
            }

            if (appProcess != null && !appProcess.HasExited)
            {
                try
                {
                    appProcess.Kill(true);
                    Log("App process terminated");
                }
                catch { }
            }
            Log("Exiting message loop");
        }

        private static unsafe Process? GetProcessByWindow(HWND hWnd)
        {
            uint processId;
            uint threadId = PInvoke.GetWindowThreadProcessId(hWnd, &processId);
            if (threadId == 0)
            {
                throw new Exception("Unable to determine process for window");
            }

            try
            {
                return Process.GetProcessById((int)processId);
            }
            catch
            {
                return null;
            }
        }

        private static LRESULT WindowProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            switch (msg)
            {
                case PInvoke.WM_PAINT:
                    if (hWnd == hostWindow && backgroundImage != null)
                    {
                        var hdc = PInvoke.BeginPaint(hWnd, out var ps);
                        using (Graphics g = Graphics.FromHdc(hdc))
                        {
                            var monitor = Utils.GetMonitorFromWindow(hWnd);
                            g.DrawImage(backgroundImage, 0, 0, monitor.Width, monitor.Height);

                            if (overlayImage != null && !string.IsNullOrEmpty(overlayPosition))
                            {
                                DrawOverlayImage(
                                    g,
                                    overlayImage,
                                    overlayPosition,
                                    monitor.Width,
                                    monitor.Height
                                );
                            }
                        }
                        PInvoke.EndPaint(hWnd, ps);
                    }
                    break;

                case PInvoke.WM_SIZE:
                    if (hWnd == hostWindow)
                    {
                        PositionAppWindow();
                        if (
                            magnificationEnabled
                            && magnifierHostWindow != HWND.Null
                            && zoomFactor > 1.0f
                        )
                        {
                            ResizeMagnifierHostWindow();
                        }
                        PInvoke.RedrawWindow(
                            hWnd,
                            null,
                            null,
                            Windows.Win32.Graphics.Gdi.REDRAW_WINDOW_FLAGS.RDW_INVALIDATE
                                | Windows.Win32.Graphics.Gdi.REDRAW_WINDOW_FLAGS.RDW_UPDATENOW
                        );
                    }
                    return (LRESULT)IntPtr.Zero;

                case PInvoke.WM_LBUTTONDOWN:
                case PInvoke.WM_RBUTTONDOWN:
                case PInvoke.WM_MBUTTONDOWN:
                    if (hWnd == hostWindow)
                    {

                        if (PInvoke.GetClientRect(hostWindow, out var clientRect))
                        {
                            int x = (short)(lParam.Value & 0xFFFF);
                            int y = (short)((lParam.Value >> 16) & 0xFFFF);


                            int appLeft,
                                appTop;
                            if (useCustomPosition)
                            {

                                appLeft = appX - currentMonitor.X;
                                appTop = appY - currentMonitor.Y;
                            }
                            else
                            {
                                int hostWidth = clientRect.right - clientRect.left;
                                int hostHeight = clientRect.bottom - clientRect.top;
                                appLeft = (hostWidth - appWidth) / 2;
                                appTop = (hostHeight - appHeight) / 2;
                            }

                            if (
                                x < appLeft
                                || x > appLeft + appWidth
                                || y < appTop
                                || y > appTop + appHeight
                            )
                            {

                                if (appWindow != HWND.Null)
                                {
                                    PInvoke.SetForegroundWindow(appWindow);
                                    PInvoke.SetFocus(appWindow);
                                }
                                return (LRESULT)IntPtr.Zero;
                            }
                        }
                    }
                    break;

                case PInvoke.WM_CLOSE:
                    if (hWnd == hostWindow)
                    {
                        PInvoke.DestroyWindow(hWnd);
                        PInvoke.PostQuitMessage(0);
                    }
                    break;

                case PInvoke.WM_TIMER:
                    if (hWnd == hostWindow)
                    {
                        if (wParam.Value == 1)
                        {
                            if (PInvoke.GetWindowRect(appWindow, out var currentRect))
                            {
                                int currentWidth = currentRect.right - currentRect.left;
                                int currentHeight = currentRect.bottom - currentRect.top;

                                if (currentWidth != appWidth || currentHeight != appHeight)
                                {
                                    OnWindowSizeChanged?.Invoke();
                                }
                            }
                        }
                        else if (
                            wParam.Value == 2
                            && magnificationEnabled
                            && magnifierWindow != HWND.Null
                            && zoomFactor > 1.0f
                        )
                        {
                            UpdateMagnifierSource();
                        }
                    }
                    break;

                case PInvoke.WM_HOTKEY:
                    if (hWnd == hostWindow && magnificationEnabled)
                    {
                        switch (wParam.Value)
                        {
                            case HOTKEY_ZOOM_IN:
                                ZoomIn();
                                break;
                            case HOTKEY_ZOOM_OUT:
                                ZoomOut();
                                break;
                            case HOTKEY_ZOOM_RESET:
                                ResetZoom();
                                break;
                        }
                    }
                    return (LRESULT)IntPtr.Zero;
            }

            return PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static void DrawOverlayImage(
            Graphics g,
            Image image,
            string position,
            int hostWidth,
            int hostHeight
        )
        {
            int x,
                y;

            switch (position)
            {
                case "center":
                    x = (hostWidth - image.Width) / 2;
                    y = (hostHeight - image.Height) / 2;
                    break;
                case "top-left":
                    x = y = 0;
                    break;
                case "top-right":
                    x = hostWidth - image.Width;
                    y = 0;
                    break;
                case "bottom-left":
                    x = 0;
                    y = hostHeight - image.Height;
                    break;
                case "bottom-right":
                    x = hostWidth - image.Width;
                    y = hostHeight - image.Height;
                    break;
                default:
                    return;
            }

            var colorMatrix = new ColorMatrix { Matrix33 = 1.0f };
            var imageAttributes = new ImageAttributes();
            imageAttributes.SetColorMatrix(
                colorMatrix,
                ColorMatrixFlag.Default,
                ColorAdjustType.Bitmap
            );

            g.DrawImage(
                image,
                new Rectangle(x, y, image.Width, image.Height),
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                imageAttributes
            );
        }

        private static bool IsValidOverlayPosition(string position)
        {
            return position == "center"
                || position == "top-left"
                || position == "top-right"
                || position == "bottom-left"
                || position == "bottom-right";
        }

        private static void Log(string message)
        {
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Console.WriteLine(logMessage);
            File.AppendAllText(logFilePath, logMessage + Environment.NewLine);
        }

        #region Win32 Constants

        private const int COLOR_BTNFACE = 15;
        private const uint MS_SHOWMAGNIFIEDCURSOR = 0x0001;

        private const int HOTKEY_ZOOM_IN = 1;
        private const int HOTKEY_ZOOM_OUT = 2;
        private const int HOTKEY_ZOOM_RESET = 3;

        #endregion
    }
}
