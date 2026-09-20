using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PulsePal.App.Services;

/// <summary>
/// A native notification-area icon. Construct, update, and dispose it on the
/// WinUI UI thread; that thread's existing message pump dispatches its commands.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private const uint RetryTimerId = 1;
    private const uint WmNull = 0x0000;
    private const uint WmClose = 0x0010;
    private const uint WmNcCreate = 0x0081;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmTimer = 0x0113;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private static readonly ConcurrentDictionary<IntPtr, TrayIcon> Windows = new();
    // Native code retains this function pointer for the lifetime of every window class.
    private static readonly Native.WindowProcedure WindowProcedure = DispatchMessage;

    private readonly Action<string> _command;
    private readonly uint _threadId;
    private readonly IntPtr _module;
    private readonly string _className = "PulsePal.TrayIcon." + Guid.NewGuid().ToString("N");
    private readonly uint _taskbarCreated;
    private IntPtr _window;
    private IntPtr _icon;
    private IntPtr _menu;
    private bool _classRegistered;
    private bool _retryTimerRunning;
    private bool _trackingMenu;
    private bool _focusActive;
    private bool _disposed;

    /// <summary>Receives native and command-handler failures on the owning UI thread.</summary>
    public event Action<Exception>? Error;

    public bool IsRegistered { get; private set; }

    /// <param name="command">
    /// Called on the owning UI thread with show, focus, breathing, controls, or exit.
    /// </param>
    public TrayIcon(Action<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _command = command;
        _threadId = Native.GetCurrentThreadId();
        _module = Native.GetModuleHandleW(null);
        if (_module == IntPtr.Zero)
            throw LastError("GetModuleHandleW");

        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        if (_taskbarCreated == 0)
            throw LastError("RegisterWindowMessageW");

        try
        {
            var windowClass = new Native.WindowClass
            {
                Procedure = WindowProcedure,
                Instance = _module,
                ClassName = _className
            };
            if (Native.RegisterClassW(ref windowClass) == 0)
                throw LastError("RegisterClassW");
            _classRegistered = true;

            var context = GCHandle.Alloc(this);
            try
            {
                // A hidden top-level tool window (not HWND_MESSAGE) receives
                // Explorer's broadcast when the taskbar is recreated.
                _window = Native.CreateWindowExW(
                    0x00000080, _className, "PulsePal tray", 0,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _module,
                    GCHandle.ToIntPtr(context));
                if (_window == IntPtr.Zero)
                    throw LastError("CreateWindowExW");
            }
            finally
            {
                context.Free();
            }

            _icon = CreateCompanionIcon();
            RegisterIcon();
        }
        catch (Exception exception)
        {
            _disposed = true;
            var cleanupErrors = ReleaseResources();
            if (cleanupErrors.Count != 0)
            {
                cleanupErrors.Insert(0, exception);
                throw new AggregateException("Tray initialization and cleanup failed.", cleanupErrors);
            }
            throw;
        }
    }

    public void SetFocusActive(bool active)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _focusActive = active;
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed)
            return;
        _disposed = true;
        foreach (var exception in ReleaseResources())
            ReportError(exception);
        GC.SuppressFinalize(this);
    }

    private void VerifyThread()
    {
        if (Native.GetCurrentThreadId() != _threadId)
            throw new InvalidOperationException("TrayIcon must be used on its creating WinUI UI thread.");
    }

    private Native.NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<Native.NotifyIconData>(),
        Window = _window,
        Id = IconId,
        Flags = 0x00000001 | 0x00000002 | 0x00000004, // MESSAGE | ICON | TIP
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "PulsePal companion",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private void RegisterIcon()
    {
        var data = IconData();
        if (!Native.Shell_NotifyIconW(0, ref data))
            throw new InvalidOperationException("Shell_NotifyIconW(NIM_ADD) failed.");

        // Keep the default (version 0) callback contract: wParam is the icon ID
        // and lParam is the mouse message, without version-4 coordinate packing.
        IsRegistered = true;
    }

    private void RestoreIcon()
    {
        try
        {
            RegisterIcon();
            StopRetryTimer();
        }
        catch (Exception exception)
        {
            ReportError(exception);
            // Explorer can broadcast before its notification area is ready.
            if (!_disposed && !_retryTimerRunning)
            {
                if (Native.SetTimer(_window, new UIntPtr(RetryTimerId), 3000, IntPtr.Zero) == UIntPtr.Zero)
                    ReportError(LastError("SetTimer"));
                else
                    _retryTimerRunning = true;
            }
        }
    }

    private void StopRetryTimer()
    {
        if (!_retryTimerRunning)
            return;
        if (!Native.KillTimer(_window, new UIntPtr(RetryTimerId)))
        {
            ReportError(new InvalidOperationException("KillTimer failed for the tray recovery timer."));
            return;
        }
        _retryTimerRunning = false;
    }

    private static IntPtr DispatchMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        TrayIcon? tray = null;
        try
        {
            if (message == WmNcCreate)
            {
                // lpCreateParams is the first pointer in CREATESTRUCTW.
                var context = GCHandle.FromIntPtr(Marshal.ReadIntPtr(lParam));
                tray = (TrayIcon?)context.Target;
                if (tray is null || !Windows.TryAdd(window, tray))
                    return IntPtr.Zero;
                tray._window = window;
            }
            else
            {
                Windows.TryGetValue(window, out tray);
            }

            if (message == WmNcDestroy)
            {
                Windows.TryRemove(window, out _);
                if (tray is not null)
                {
                    tray._window = IntPtr.Zero;
                    tray._retryTimerRunning = false;
                    tray.IsRegistered = false;
                }
            }
            else if (tray is not null && tray.HandleMessage(message, wParam, lParam))
            {
                return IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            // Exceptions must never cross the unmanaged window-procedure boundary.
            if (tray is not null)
                tray.ReportError(exception);
            else
                Trace.TraceError("PulsePal tray window: {0}", exception);
            if (message == WmNcCreate)
                return IntPtr.Zero;
            return IntPtr.Zero;
        }

        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    private bool HandleMessage(uint message, UIntPtr wParam, IntPtr lParam)
    {
        // A broadcast WM_CLOSE must not silently destroy the tray's owner.
        if (message == WmClose)
            return true;
        if (_disposed)
            return false;

        if (message == _taskbarCreated)
        {
            IsRegistered = false;
            RestoreIcon();
            return true;
        }
        if (message == WmTimer && wParam.ToUInt64() == RetryTimerId)
        {
            if (!IsRegistered)
                RestoreIcon();
            else
                StopRetryTimer();
            return true;
        }
        if (message != CallbackMessage || wParam.ToUInt64() != IconId)
            return false;

        switch (unchecked((uint)lParam.ToInt64()))
        {
            case WmLeftButtonUp:
            case NinSelect:
            case NinKeySelect:
                _command("show");
                break;
            case WmRightButtonUp:
            case WmContextMenu:
                ShowMenu();
                break;
        }
        return true;
    }

    private void ShowMenu()
    {
        if (_menu != IntPtr.Zero)
            return;

        _menu = Native.CreatePopupMenu();
        if (_menu == IntPtr.Zero)
            throw LastError("CreatePopupMenu");

        uint selection = 0;
        try
        {
            AppendMenu(1, "Show companion");
            AppendMenu(2, _focusActive ? "End focus" : "Start focus");
            AppendMenu(3, "Guided breathing · 1 min");
            AppendMenu(6, "Meet your companion / Profile & preferences");
            AppendMenu(7, "Screen break · 5 min");
            AppendMenu(8, "Water break · 1 min");
            AppendMenu(9, "Gentle movement · 2 min");
            AppendMenu(4, "Demo controls\u2026");
            if (!Native.AppendMenuW(_menu, 0x00000800, UIntPtr.Zero, null))
                throw LastError("AppendMenuW(separator)");
            AppendMenu(5, "Exit");

            if (!Native.GetCursorPos(out var position))
                throw LastError("GetCursorPos");
            if (!Native.SetForegroundWindow(_window))
                ReportError(new InvalidOperationException("SetForegroundWindow failed for the tray menu."));

            if (!_disposed)
            {
                // RETURNCMD | NONOTIFY | RIGHTBUTTON. Cancellation returns zero.
                _trackingMenu = true;
                try
                {
                    selection = Native.TrackPopupMenuEx(
                        _menu, 0x00000100 | 0x00000080 | 0x00000002,
                        position.X, position.Y, _window, IntPtr.Zero);
                }
                finally
                {
                    _trackingMenu = false;
                }
            }
        }
        finally
        {
            // Required by the shell popup-menu protocol so the next menu dismisses correctly.
            if (_window != IntPtr.Zero && !Native.PostMessageW(_window, WmNull, UIntPtr.Zero, IntPtr.Zero))
                ReportError(LastError("PostMessageW(WM_NULL)"));
            if (_menu != IntPtr.Zero)
            {
                if (!Native.DestroyMenu(_menu))
                    ReportError(LastError("DestroyMenu"));
                else
                    _menu = IntPtr.Zero;
            }
        }

        if (_disposed)
            return;
        var command = selection switch
        {
            1 => "show",
            2 => "focus",
            3 => "breathing",
            4 => "controls",
            5 => "exit",
            6 => "profile",
            7 => "screen-break",
            8 => "water-break",
            9 => "stretch-break",
            _ => null
        };
        if (command is not null)
            _command(command);
    }

    private void AppendMenu(uint id, string label)
    {
        if (!Native.AppendMenuW(_menu, 0, new UIntPtr(id), label))
            throw LastError("AppendMenuW");
    }

    private List<Exception> ReleaseResources()
    {
        var errors = new List<Exception>();
        if (_menu != IntPtr.Zero)
        {
            // TrackPopupMenuEx owns a nested message loop; its finally block
            // destroys the menu after EndMenu unwinds that loop.
            if (_trackingMenu)
            {
                if (!Native.EndMenu())
                    errors.Add(new InvalidOperationException("EndMenu failed."));
            }
            else if (!Native.DestroyMenu(_menu))
            {
                errors.Add(LastError("DestroyMenu"));
            }
            else
            {
                _menu = IntPtr.Zero;
            }
        }
        if (_retryTimerRunning && _window != IntPtr.Zero)
        {
            if (!Native.KillTimer(_window, new UIntPtr(RetryTimerId)))
                errors.Add(new InvalidOperationException("KillTimer failed."));
            else
                _retryTimerRunning = false;
        }
        if (IsRegistered)
        {
            var data = IconData();
            if (!Native.Shell_NotifyIconW(2, ref data))
                errors.Add(new InvalidOperationException("Shell_NotifyIconW(NIM_DELETE) failed."));
            IsRegistered = false;
        }
        if (_window != IntPtr.Zero)
        {
            if (!Native.DestroyWindow(_window))
                errors.Add(LastError("DestroyWindow"));
        }
        if (_icon != IntPtr.Zero)
        {
            if (!Native.DestroyIcon(_icon))
                errors.Add(LastError("DestroyIcon"));
            else
                _icon = IntPtr.Zero;
        }
        if (_classRegistered && _window == IntPtr.Zero)
        {
            if (!Native.UnregisterClassW(_className, _module))
                errors.Add(LastError("UnregisterClassW"));
            else
                _classRegistered = false;
        }
        return errors;
    }

    private void ReportError(Exception exception)
    {
        Trace.TraceError("PulsePal tray: {0}", exception);
        var handlers = Error;
        if (handlers is null)
            return;
        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(exception);
            }
            catch (Exception subscriberError)
            {
                Trace.TraceError("PulsePal tray Error subscriber: {0}", subscriberError);
            }
        }
    }

    private static Win32Exception LastError(string operation) =>
        new(Marshal.GetLastWin32Error(), operation + " failed.");

    private IntPtr CreateCompanionIcon()
    {
        const int size = 32;
        var bitmapInfo = new Native.BitmapInfo
        {
            Header = new Native.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
                Width = size,
                Height = -size,
                Planes = 1,
                BitCount = 32,
                SizeImage = size * size * 4
            }
        };
        var color = Native.CreateDIBSection(
            IntPtr.Zero, ref bitmapInfo, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero)
            throw new InvalidOperationException("CreateDIBSection failed for the tray icon.");

        IntPtr mask = IntPtr.Zero;
        try
        {
            if (bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection returned no pixel buffer.");
            var pixels = RenderCompanion(size);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            // A monochrome mask has WORD-aligned rows; zero means opaque.
            mask = Native.CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
            if (mask == IntPtr.Zero)
                throw new InvalidOperationException("CreateBitmap failed for the tray icon mask.");

            var info = new Native.IconInfo { IsIcon = true, Mask = mask, Color = color };
            var icon = Native.CreateIconIndirect(ref info);
            if (icon == IntPtr.Zero)
                throw LastError("CreateIconIndirect");
            return icon;
        }
        finally
        {
            // CreateIconIndirect copies both bitmaps; the HICON owns its own pixels.
            if (mask != IntPtr.Zero && !Native.DeleteObject(mask))
                ReportError(new InvalidOperationException("DeleteObject failed for the tray icon mask."));
            if (!Native.DeleteObject(color))
                ReportError(new InvalidOperationException("DeleteObject failed for the tray icon bitmap."));
        }
    }

    private static int[] RenderCompanion(int size)
    {
        const int samples = 4;
        var pixels = new int[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var alpha = 0;
                var red = 0;
                var green = 0;
                var blue = 0;
                for (var sy = 0; sy < samples; sy++)
                {
                    for (var sx = 0; sx < samples; sx++)
                    {
                        var px = (x + (sx + 0.5) / samples) * 32 / size;
                        var py = (y + (sy + 0.5) / samples) * 32 / size;
                        var rgb = CompanionPixel(px, py);
                        if (rgb < 0)
                            continue;
                        alpha += 255;
                        red += (rgb >> 16) & 255;
                        green += (rgb >> 8) & 255;
                        blue += rgb & 255;
                    }
                }
                // BGRA in little-endian memory, with premultiplied alpha.
                const int count = samples * samples;
                pixels[y * size + x] = unchecked((int)(
                    ((uint)(alpha / count) << 24) |
                    ((uint)(red / count) << 16) |
                    ((uint)(green / count) << 8) |
                    (uint)(blue / count)));
            }
        }
        return pixels;
    }

    private static int CompanionPixel(double x, double y)
    {
        // Original artwork: a cyan circular robot, smiling faceplate, and star antenna.
        var color = -1;
        var dx = x - 16;
        var dy = y - 18;
        if (dx * dx + dy * dy <= 12.5 * 12.5)
            color = 0x07566B;
        if (dx * dx + dy * dy <= 11.3 * 11.3)
            color = y < 18 ? 0x67E8F9 : 0x22C9DF;
        if (x >= 14.8 && x <= 17.2 && y >= 4 && y <= 8)
            color = 0x67E8F9;
        if (Math.Sqrt(Math.Abs(x - 16)) + Math.Sqrt(Math.Abs(y - 4)) <= Math.Sqrt(3.5))
            color = 0xD9FCFF;
        // Rounded faceplate, evaluated as a rounded rectangle.
        var rx = Math.Max(Math.Abs(x - 16) - 6, 0);
        var ry = Math.Max(Math.Abs(y - 18) - 3, 0);
        if (rx * rx + ry * ry <= 2.5 * 2.5)
            color = 0x083F57;
        if ((x - 12.3) * (x - 12.3) + (y - 16.5) * (y - 16.5) <= 1.4 * 1.4 ||
            (x - 19.7) * (x - 19.7) + (y - 16.5) * (y - 16.5) <= 1.4 * 1.4)
            color = 0xE4FDFF;
        var smile = (x - 16) * (x - 16) + (y - 18.3) * (y - 18.3);
        if (y > 19 && smile >= 2.2 * 2.2 && smile <= 3.3 * 3.3)
            color = 0x67E8F9;
        return color;
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Style;
            internal WindowProcedure Procedure;
            internal int ClassExtra;
            internal int WindowExtra;
            internal IntPtr Instance;
            internal IntPtr Icon;
            internal IntPtr Cursor;
            internal IntPtr Background;
            internal string? MenuName;
            internal string ClassName;
        }

        // Full NOTIFYICONDATAW, using native pointer alignment (976 bytes on x64).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NotifyIconData
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Id;
            internal uint Flags;
            internal uint CallbackMessage;
            internal IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string Tip;
            internal uint State;
            internal uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            internal string Info;
            internal uint TimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            internal string InfoTitle;
            internal uint InfoFlags;
            internal Guid GuidItem;
            internal IntPtr BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width;
            internal int Height;
            internal ushort Planes;
            internal ushort BitCount;
            internal uint Compression;
            internal uint SizeImage;
            internal int XPixelsPerMeter;
            internal int YPixelsPerMeter;
            internal uint ColorsUsed;
            internal uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfo
        {
            internal BitmapInfoHeader Header;
            internal uint Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)]
            internal bool IsIcon;
            internal uint HotspotX;
            internal uint HotspotY;
            internal IntPtr Mask;
            internal IntPtr Color;
        }

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr GetModuleHandleW(string? moduleName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern uint RegisterWindowMessageW(string message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern ushort RegisterClassW(ref WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClassW(string className, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr CreateWindowExW(
            uint extendedStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu,
            IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern IntPtr DefWindowProcW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr window);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(uint operation, ref NotifyIconData data);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EndMenu();
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool KillTimer(IntPtr window, UIntPtr id);
        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateDIBSection(
            IntPtr deviceContext, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr icon);
    }
}
