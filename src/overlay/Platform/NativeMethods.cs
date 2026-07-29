using System;
using System.Runtime.InteropServices;

namespace DragonSwordTreasureRadar
{
    internal static class NativeMethods
    {
        private const string SqlCipherLibrary =
            "e_sqlcipher.dll";

        public const int WsExTransparent = 0x20;
        public const int WsExToolWindow = 0x80;
        public const int WsExLayered = 0x80000;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpShowWindow = 0x0040;
        public static readonly IntPtr HwndTopmost =
            new IntPtr(-1);

        public delegate bool EnumWindowsCallback(
            IntPtr window,
            IntPtr parameter);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ExecCallback(
            IntPtr context,
            int columnCount,
            IntPtr values,
            IntPtr names);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(
            int awareness);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(
            EnumWindowsCallback callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(
            IntPtr window,
            out NativeRect rectangle);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(
            IntPtr window,
            out NativeRect rectangle);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(
            IntPtr window,
            ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(
            IntPtr window);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(
            uint access,
            bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(
            IntPtr process,
            IntPtr address,
            byte[] buffer,
            IntPtr size,
            out IntPtr bytesRead);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport(
            SqlCipherLibrary,
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2(
            byte[] filename,
            out IntPtr database,
            int flags,
            IntPtr vfs);

        [DllImport(
            SqlCipherLibrary,
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_exec(
            IntPtr database,
            byte[] sql,
            ExecCallback callback,
            IntPtr context,
            out IntPtr error);

        [DllImport(
            SqlCipherLibrary,
            CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_errmsg(
            IntPtr database);

        [DllImport(
            SqlCipherLibrary,
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close_v2(
            IntPtr database);

        [DllImport(
            SqlCipherLibrary,
            CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3_free(
            IntPtr pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width
        {
            get { return Right - Left; }
        }

        public int Height
        {
            get { return Bottom - Top; }
        }
    }
}
