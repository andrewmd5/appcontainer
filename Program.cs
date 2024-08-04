using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace AppContainer
{
    internal partial class Program
    {
        private static readonly Windows.Win32.UI.WindowsAndMessaging.WNDPROC _wndProc = new Windows.Win32.UI.WindowsAndMessaging.WNDPROC(WindowProc);
        private static Process? appProcess;
        private static HWND hostWindow;
        private static Bitmap? backgroundImage;
        private static HWND appWindow;
        private static int appWidth;
        private static int appHeight;
        private static readonly string logFilePath = "AppContainer.log";

        private delegate void WindowSizeChanged();
        private static event WindowSizeChanged? OnWindowSizeChanged;

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                var arguments = Utils.ParseArguments(args);
#if DEBUG
                PInvoke.AttachConsole(unchecked((uint)-1));
#endif


                Log("Application started");

                // Load the background image or set the background color
                if (arguments.TryGetValue("background-image", out string? backgroundImagePath))
                {
                    if (!File.Exists(backgroundImagePath))
                    {
                        throw new FileNotFoundException($"Background image not found at the specified path: {backgroundImagePath}. Please ensure the file exists and the path is correct.");
                    }
                    backgroundImage = new Bitmap(backgroundImagePath);
                    Log($"Background image loaded: {backgroundImagePath}");
                }
                else if (arguments.TryGetValue("background-color", out string? backgroundColorHex))
                {
                    if (!Utils.IsValidHexColor(backgroundColorHex))
                    {
                        throw new ArgumentException("Invalid background color hex value. Please provide a valid hex color code.");
                    }
                    Color backgroundColor = ColorTranslator.FromHtml(backgroundColorHex);
                    backgroundImage = Utils.CreateSolidColorBitmap(backgroundColor,
                        PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXSCREEN),
                        PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYSCREEN));
                    Log($"Background color set: {backgroundColorHex}");
                }
                else if (arguments.TryGetValue("background-gradient", out string? gradientColors))
                {
                    var colors = gradientColors.Split(';');
                    if (colors.Length != 2 || !Utils.IsValidHexColor(colors[0]) || !Utils.IsValidHexColor(colors[1]))
                    {
                        throw new ArgumentException("Invalid background gradient value. Please provide two valid hex color codes separated by a semicolon.");
                    }
                    Color color1 = ColorTranslator.FromHtml(colors[0]);
                    Color color2 = ColorTranslator.FromHtml(colors[1]);
                    backgroundImage = Utils.CreateGradientBitmap(color1, color2,
                        PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXSCREEN),
                        PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYSCREEN));
                    Log($"Background gradient set from {colors[0]} to {colors[1]}");
                }
                else
                {
                    throw new ArgumentException("Background image or background color argument is missing. Please provide a valid path using the 'background-image' argument or a valid hex color code using the 'background-color' argument.");
                }

                // Create the host window
                hostWindow = CreateHostWindow();
                if (hostWindow == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create host window. Please check if you have sufficient permissions and system resources.");
                }
                Log("Host window created successfully");

                if (arguments.TryGetValue("window-title", out var appWindowTitle))
                {

                    appWindow = PInvoke.FindWindow(null, appWindowTitle);
                    if (appWindow == IntPtr.Zero)
                    {
                        throw new InvalidOperationException($"Could not find a window with the title: '{appWindowTitle}'. Please ensure the app window is open and the title is correct.");
                    }
                }
                else if (arguments.TryGetValue("window-handle", out var windowHandle))
                {
                    appWindow = Utils.ConvertAndValidateWindowHandle(windowHandle);
                }
                else
                {
                    throw new ArgumentException("Neither 'window-title' nor 'window-handle' argument provided. Please specify either the app window title or handle.");
                }

                appProcess = GetProcessByWindow(appWindow);
                if (appProcess == null)
                {
                    throw new InvalidOperationException("Failed to find the app process. Please ensure the app is running and accessible.");
                }
                Log($"App process found: {appProcess.ProcessName} (ID: {appProcess.Id})");

                if (!arguments.TryGetValue("width", out var width))
                {
                    throw new ArgumentException("Width argument is missing. Please provide a valid width using the 'width' argument.");
                }
                if (!arguments.TryGetValue("height", out var height))
                {
                    throw new ArgumentException("Height argument is missing. Please provide a valid height using the 'height' argument.");
                }

                // Set desired app window size 
                // (0 means fullscreen, -1 means use existing size)
                if (!int.TryParse(width, out appWidth) || !int.TryParse(height, out appHeight))
                {
                    throw new ArgumentException("Invalid width or height value. Please provide valid integer values for width and height.");
                }
                if (appWidth < -1 || appHeight < -1)
                {
                    throw new ArgumentOutOfRangeException("width/height", "Invalid width or height value. Values must be -1 (use existing size), 0 (fullscreen), or a positive integer.");
                }

                // Set the host window title and icon to match the app window
                UpdateHostWindowTitleAndIcon();

                // Embed the app window as a child of the host window
                EmbedAppWindow();

                // Monitor the app process exit
                appProcess.EnableRaisingEvents = true;
                appProcess.Exited += (sender, e) =>
                {
                    Log("App process exited.");
                    PInvoke.PostMessage(hostWindow, WM_CLOSE, 0, IntPtr.Zero);
                };

                // Subscribe to the window size changed event
                OnWindowSizeChanged += HandleWindowSizeChanged;

                // Run a message loop to keep the host window responsive
                RunMessageLoop();
            }
            catch (Exception ex)
            {
                Log($"Fatal error: {ex.Message}\n{ex.StackTrace}");
                string errorMessage = $"An error occurred: {ex.Message}\n\nFor more details, please check the log file at: {logFilePath}";
                PInvoke.MessageBox(HWND.Null, errorMessage, "Error", Windows.Win32.UI.WindowsAndMessaging.MESSAGEBOX_STYLE.MB_OK | Windows.Win32.UI.WindowsAndMessaging.MESSAGEBOX_STYLE.MB_ICONERROR);
            }
            finally
            {
                if (backgroundImage != null)
                {
                    backgroundImage.Dispose();
                }
                Log("Application ended");

#if DEBUG
                PInvoke.FreeConsole();
#endif

                Environment.Exit(0);
            }
        }

        /// <summary>
        /// Creates the host window for the application.
        /// </summary>
        /// <returns>A handle to the created window, or IntPtr.Zero if the creation fails.</returns>
        /// <exception cref="Exception">Thrown when window class registration or window creation fails.</exception>
        private unsafe static HWND CreateHostWindow()
        {
            HINSTANCE hInstance = new(Process.GetCurrentProcess().Handle);
            if (hInstance == IntPtr.Zero)
            {
                throw new Exception($"Failed to get module handle. Error code: {Marshal.GetLastWin32Error()}");
            }

            const string WindowClassName = "AppContainerClass";
            const string WindowName = "AppContainer";

            ushort classId;
            fixed (char* pClassName = WindowClassName)
            fixed (char* pWindowName = WindowName)
            {
                Windows.Win32.UI.WindowsAndMessaging.WNDCLASSEXW wndClass = new Windows.Win32.UI.WindowsAndMessaging.WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<Windows.Win32.UI.WindowsAndMessaging.WNDCLASSEXW>(),
                    lpfnWndProc = _wndProc,
                    hInstance = hInstance,
                    lpszClassName = pClassName,
                    hbrBackground = HBRUSH.Null
                };

                classId = PInvoke.RegisterClassEx(in wndClass);
                if (classId == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Exception($"Failed to register window class. Error code: {error}");
                }

                Log($"Window class registered successfully. Class ID: {classId}");

                HWND hwnd = PInvoke.CreateWindowEx(
                    0,
                    pClassName,
                    pWindowName,
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP | Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_VISIBLE,
                    PInvoke.CW_USEDEFAULT,
                    PInvoke.CW_USEDEFAULT,
                    PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXSCREEN),
                    PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYSCREEN),
                    HWND.Null,
                    Windows.Win32.UI.WindowsAndMessaging.HMENU.Null,
                    hInstance,
                    null);

                if (hwnd == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Exception($"Failed to create window. Error code: {error}");
                }

                Log("Window created successfully");
                return hwnd;
            }
        }

        /// <summary>
        /// Embeds the app window as a child of the host window.
        /// </summary>
        /// <exception cref="Exception">Thrown when setting the parent window fails.</exception>
        private static void EmbedAppWindow()
        {
            // Remove the app window's title bar and border

            var style = PInvoke.GetWindowLong(appWindow, Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE | WS_MAXIMIZE | WS_SYSMENU);
            if (PInvoke.SetWindowLong(appWindow, Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_STYLE, style) == 0)
            {
                Log($"Warning: Failed to set window style. Error code: {Marshal.GetLastWin32Error()}");
            }

            if (PInvoke.SetParent(appWindow, hostWindow) == HWND.Null)
            {
                throw new Exception($"Failed to set parent window. Error code: {Marshal.GetLastWin32Error()}");
            }

            // Determine the size to use for the app window
            DetermineAppWindowSize();

            // Center and resize the app window
            CenterAndResizeAppWindow();

            // Set up a timer to periodically check for window size changes
            if (PInvoke.SetTimer(hostWindow, 1, 100, null) == default)
            {
                Log($"Warning: Failed to set timer. Error code: {Marshal.GetLastWin32Error()}");
            }

            Log("App window embedded successfully");
        }

        /// <summary>
        /// Determines the size to use for the app window based on the provided arguments.
        /// </summary>
        private static void DetermineAppWindowSize()
        {
            if (appWidth == 0 && appHeight == 0)
            {
                // Use fullscreen (host window size) if both width and height are 0
                if (!PInvoke.GetClientRect(hostWindow, out var hostRect))
                {
                    Log($"Warning: Failed to get host window rect. Error code: {Marshal.GetLastWin32Error()}");
                    return;
                }
                appWidth = hostRect.right - hostRect.left;
                appHeight = hostRect.bottom - hostRect.top;
                Log($"Using fullscreen size: {appWidth}x{appHeight}");
            }
            else if (appWidth == -1 && appHeight == -1)
            {
                // Use existing app window size if both width and height are -1
                if (!PInvoke.GetWindowRect(appWindow, out var appRect))
                {
                    Log($"Warning: Failed to get app window rect. Error code: {Marshal.GetLastWin32Error()}");
                    return;
                }
                appWidth = appRect.right - appRect.left;
                appHeight = appRect.bottom - appRect.top;
                Log($"Using existing app window size: {appWidth}x{appHeight}");
            }
            else
            {
                // Use the specified size
                Log($"Using specified app window size: {appWidth}x{appHeight}");
            }
        }
        /// <summary>
        /// Handles changes in the app window size.
        /// </summary>
        private static void HandleWindowSizeChanged()
        {
            if (!PInvoke.GetWindowRect(appWindow, out var appRect))
            {
                Log($"Warning: Failed to get window rect. Error code: {Marshal.GetLastWin32Error()}");
                return;
            }

            int newWidth = appRect.right - appRect.left;
            int newHeight = appRect.bottom - appRect.top;

            // Only update if the size has actually changed
            if (newWidth != appWidth || newHeight != appHeight)
            {
                appWidth = newWidth;
                appHeight = newHeight;

                CenterAndResizeAppWindow();
                PInvoke.RedrawWindow(hostWindow, null, null, Windows.Win32.Graphics.Gdi.REDRAW_WINDOW_FLAGS.RDW_INVALIDATE | Windows.Win32.Graphics.Gdi.REDRAW_WINDOW_FLAGS.RDW_UPDATENOW);
                Log($"Window size changed: {appWidth}x{appHeight}");
            }
        }

        /// <summary>
        /// Centers and resizes the app window within the host window.
        /// </summary>
        private static void CenterAndResizeAppWindow()
        {
            if (!PInvoke.GetClientRect(hostWindow, out var clientRect))
            {
                Log($"Warning: Failed to get client rect. Error code: {Marshal.GetLastWin32Error()}");
                return;
            }

            int hostWidth = clientRect.right - clientRect.left;
            int hostHeight = clientRect.bottom - clientRect.top;

            int x = (hostWidth - appWidth) / 2;
            int y = (hostHeight - appHeight) / 2;

            if (!PInvoke.MoveWindow(appWindow, x, y, appWidth, appHeight, true))
            {
                Log($"Warning: Failed to move window. Error code: {Marshal.GetLastWin32Error()}");
            }
        }

        /// <summary>
        /// Updates the host window's title and icon to match the app window.
        /// </summary>
        private unsafe static void UpdateHostWindowTitleAndIcon()
        {
            // Get the app window title
            int bufferSize = PInvoke.GetWindowTextLength(appWindow) + 1;
            //ADDRESS WONT CHANGE, THIS VARIABLE SHOULD BE USED WITH IN THIS SCOPE.
            fixed (char* windowNameChars = new char[bufferSize])
            {
                if (PInvoke.GetWindowText(appWindow, windowNameChars, bufferSize) == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 0)  // Only log if there's an actual error, not just an empty title
                    {
                        Log($"Warning: Failed to get window text. Error code: {error}");
                    }
                }
                else
                {
                    string appWindowTitle = new(windowNameChars);
                    // Log the title for debugging
                    Log($"Retrieved window title: {appWindowTitle}");
                    if (!PInvoke.SetWindowText(hostWindow, appWindowTitle))
                    {
                        Log($"Warning: Failed to set window text. Error code: {Marshal.GetLastWin32Error()}");
                    }
                    else
                    {
                        Log($"Successfully set host window title to: {appWindowTitle}");
                    }
                }

            }

            // Get the app window icon
            IntPtr hIcon = PInvoke.SendMessage(appWindow, WM_GETICON, ICON_BIG, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
            {
                nuint classLongPtr = PInvoke.GetClassLongPtr(appWindow, Windows.Win32.UI.WindowsAndMessaging.GET_CLASS_LONG_INDEX.GCL_HICON);
                hIcon = (IntPtr)classLongPtr;
            }
            if (hIcon != IntPtr.Zero)
            {
                PInvoke.SendMessage(hostWindow, WM_SETICON, ICON_BIG, hIcon);
            }
            else
            {
                Log("Warning: Failed to get window icon.");
            }

            Log("Host window title and icon updated");
        }


        /// <summary>
        /// Runs the main message loop for the application.
        /// </summary>
        private static void RunMessageLoop()
        {
            Log("Entering message loop");
            while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
            {
                PInvoke.TranslateMessage(msg);
                PInvoke.DispatchMessage(msg);
            }

            // Cleanup and kill the app process if still running
            if (appProcess != null && !appProcess.HasExited)
            {
                try
                {
                    appProcess.Kill();
                    Log("App process terminated");
                }
                catch (Exception ex)
                {
                    Log($"Error terminating app process: {ex.Message}");
                }
            }
            Log("Exiting message loop");
        }

        /// <summary>
        /// Retrieves the Process object associated with a given window handle.
        /// </summary>
        /// <param name="hWnd">The handle of the window.</param>
        /// <returns>The Process object associated with the window, or null if not found.</returns>
        private unsafe static Process? GetProcessByWindow(HWND hWnd)
        {
            uint processId;
            uint threadId = PInvoke.GetWindowThreadProcessId(hWnd, &processId);
            if (threadId is 0)
            {
                throw new Exception("Unable to determine process for supplied window");
            }
            try
            {
                return Process.GetProcessById((int)processId);
            }
            catch (ArgumentException)
            {
                Log($"Warning: Process with ID {processId} not found.");
                return null;
            }
        }

        /// <summary>
        /// The window procedure for handling window messages.
        /// </summary>
        /// <param name="hWnd">A handle to the window.</param>
        /// <param name="msg">The message.</param>
        /// <param name="wParam">Additional message information.</param>
        /// <param name="lParam">Additional message information.</param>
        /// <returns>The result of the message processing.</returns>
        private static LRESULT WindowProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            switch (msg)
            {
                case WM_PAINT:
                    if (backgroundImage is not null)
                    {
                        var hdc = PInvoke.BeginPaint(hWnd, out var ps);
                        using (Graphics g = Graphics.FromHdc(hdc))
                        {
                            int screenWidth = PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXSCREEN);
                            int screenHeight = PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYSCREEN);
                            g.DrawImage(backgroundImage, 0, 0, screenWidth, screenHeight);
                        }
                        PInvoke.EndPaint(hWnd, ps);
                    }
                    break;

                case WM_SIZE:
                    // Handle resize event
                    CenterAndResizeAppWindow();
                    PInvoke.RedrawWindow(hWnd, null, null, Windows.Win32.Graphics.Gdi.REDRAW_WINDOW_FLAGS.RDW_INVALIDATE | Windows.Win32.Graphics.Gdi.REDRAW_WINDOW_FLAGS.RDW_UPDATENOW);
                    return (LRESULT)IntPtr.Zero;

                case WM_CLOSE:
                    // Cleanup and exit the host window
                    PInvoke.DestroyWindow(hWnd);
                    PInvoke.PostQuitMessage(0);
                    break;

                case WM_TIMER:
                    // Check for window size changes

                    if (PInvoke.GetWindowRect(appWindow, out var currentRect))
                    {
                        int currentWidth = currentRect.right - currentRect.left;
                        int currentHeight = currentRect.bottom - currentRect.top;

                        if (currentWidth != appWidth || currentHeight != appHeight)
                        {
                            OnWindowSizeChanged?.Invoke();
                        }
                    }
                    else
                    {
                        Log($"Warning: Failed to get window rect in WM_TIMER. Error code: {Marshal.GetLastWin32Error()}");
                    }
                    break;
            }

            return PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Logs a message to the console and, in debug mode, to a file.
        /// </summary>
        /// <param name="message">The message to log.</param>
        private static void Log(string message)
        {

#if DEBUG
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Console.WriteLine(logMessage);
            File.AppendAllText(logFilePath, logMessage + Environment.NewLine);
#endif

        }


        #region Win32 API

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CAPTION = 0xC00000;
        private const int WS_THICKFRAME = 0x40000;
        private const int WS_MINIMIZE = 0x20000000;
        private const int WS_MAXIMIZE = 0x1000000;
        private const int WS_SYSMENU = 0x80000;

        private const int GWL_STYLE = -16;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_SIZE = 0x0005;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_TIMER = 0x0113;
        private const uint WM_GETICON = 0x007F;
        private const uint WM_SETICON = 0x0080;
        private const int ICON_BIG = 1;
        private const int GCL_HICON = -14;

        #endregion
    }
}
