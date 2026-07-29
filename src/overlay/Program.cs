using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DragonSwordTreasureRadar
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            DpiAwareness.Enable();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                ErrorLog.Write("UI exception", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                ErrorLog.Write("Unhandled exception", e.ExceptionObject as Exception);
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new RadarForm());
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Fatal startup exception", exception);
                MessageBox.Show(
                    "Treasure Radar could not start.\r\n\r\n" + exception.Message +
                    "\r\n\r\nDetails: " + ErrorLog.Path,
                    "DragonSword Treasure Radar",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
    }

    internal static class DpiAwareness
    {
        private static readonly IntPtr PerMonitorAwareV2 =
            new IntPtr(-4);

        public static void Enable()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(PerMonitorAwareV2))
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                const int ProcessPerMonitorDpiAware = 2;
                if (SetProcessDpiAwareness(
                    ProcessPerMonitorDpiAware) == 0)
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(
            int awareness);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }

    internal static class ErrorLog
    {
        public static readonly string Path = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "DragonSwordTreasureRadar.log"
        );

        public static void Write(string context, Exception exception)
        {
            try
            {
                File.AppendAllText(
                    Path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + context +
                    Environment.NewLine + (exception == null ? "(no exception details)" : exception.ToString()) +
                    Environment.NewLine + Environment.NewLine
                );
            }
            catch
            {
                // Logging must never terminate the overlay.
            }
        }

        public static void WriteInfo(string message)
        {
            try
            {
                File.AppendAllText(
                    Path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " +
                    message + Environment.NewLine
                );
            }
            catch
            {
                // Logging must never terminate the overlay.
            }
        }
    }

    internal static class GameProcessFinder
    {
        public static Process FindNewest()
        {
            Process[] candidates = Process.GetProcessesByName(
                "DSClient-Win64-Shipping"
            );
            Process selected = null;
            DateTime selectedStartTime = DateTime.MinValue;

            foreach (Process candidate in candidates)
            {
                try
                {
                    DateTime startTime = candidate.StartTime;
                    if (selected == null || startTime > selectedStartTime)
                    {
                        if (selected != null)
                        {
                            selected.Dispose();
                        }
                        selected = candidate;
                        selectedStartTime = startTime;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }
                catch
                {
                    candidate.Dispose();
                }
            }
            return selected;
        }
    }

    internal static class GeometryLog
    {
        public static readonly string Path = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "DragonSwordTreasureRadarGeometry.log"
        );

        public static void Write(string message)
        {
            try
            {
                File.AppendAllText(
                    Path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " +
                    message + Environment.NewLine
                );
            }
            catch
            {
                // Diagnostic logging must never terminate the overlay.
            }
        }
    }

    internal sealed class RadarForm : Form
    {
        // DragonSword's circular minimap is inset slightly from the game
        // window's right edge. The window itself stays fully transparent;
        // only treasure dots are painted over the game's minimap.
        private const float ReferenceWindowHeight = 1440f;
        private const int ReferenceOverlaySize = 360;
        private const int ReferenceRightMargin = 40;
        private const int ReferenceTopMargin = 37;
        private const float ReferenceRadarRadius = 170f;
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;
        private const int WsExLayered = 0x80000;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private static readonly IntPtr HwndTopmost =
            new IntPtr(-1);

        private readonly Timer _timer;
        private readonly string _statePath;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly NotifyIcon _trayIcon;
        private readonly TreasureSaveState _saveState = new TreasureSaveState();
        private RadarState _state;
        private DateTime _lastStateWriteUtc;
        private float _displayScale = 1f;
        private int _overlaySize = ReferenceOverlaySize;
        private string _lastGeometryLog;

        public RadarForm()
        {
            _statePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "radar_state.json"
            );
            UpdateGeometry(Screen.PrimaryScreen.Bounds.Height);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            ToolStripMenuItem exitItem = new ToolStripMenuItem(
                "Exit DragonSword Treasure Radar"
            );
            exitItem.Click += delegate
            {
                _trayIcon.Visible = false;
                Close();
                Application.Exit();
            };
            trayMenu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = SystemIcons.Application;
            _trayIcon.Text = "DragonSword Treasure Radar";
            _trayIcon.ContextMenuStrip = trayMenu;
            _trayIcon.Visible = true;

            _timer = new Timer();
            _timer.Interval = 100;
            _timer.Tick += delegate
            {
                try
                {
                    RefreshState();
                }
                catch (Exception exception)
                {
                    ErrorLog.Write("Radar refresh failed", exception);
                }
            };
            _timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            base.OnFormClosed(e);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExLayered;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RadarState state = _state;
            if (state == null || !state.enabled || state.radius <= 0)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float center = _overlaySize / 2f;
            float radarRadius = ReferenceRadarRadius * _displayScale;

            List<RadarPoint> points = (state.points ?? new List<RadarPoint>())
                .Where(point => !_saveState.IsOpened(point.saveId))
                .ToList();
            for (int index = points.Count - 1; index >= 0; index--)
            {
                RadarPoint point = points[index];
                // The in-game minimap is north-up and does not rotate.
                float x = center + (float)(point.dx / state.radius * radarRadius);
                float y = center + (float)(point.dy / state.radius * radarRadius);
                bool nearest = index == 0;
                float diameter = (nearest ? 16f : 10f) * _displayScale;
                Color color = nearest
                    ? Color.FromArgb(255, 255, 190, 45)
                    : Color.FromArgb(235, 90, 235, 255);
                using (Brush brush = new SolidBrush(color))
                using (Pen outline = new Pen(
                    Color.FromArgb(235, 5, 12, 22),
                    Math.Max(1f, 2f * _displayScale)))
                {
                    e.Graphics.FillEllipse(
                        brush,
                        x - diameter / 2,
                        y - diameter / 2,
                        diameter,
                        diameter
                    );
                    e.Graphics.DrawEllipse(
                        outline,
                        x - diameter / 2,
                        y - diameter / 2,
                        diameter,
                        diameter
                    );
                }
            }
        }

        private void RefreshState()
        {
            MoveOverGameWindow();
            _saveState.Refresh();
            try
            {
                if (!File.Exists(_statePath))
                {
                    _state = null;
                    Invalidate();
                    return;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(_statePath);
                if (writeTime != _lastStateWriteUtc)
                {
                    _lastStateWriteUtc = writeTime;
                    string json = File.ReadAllText(_statePath);
                    _state = _serializer.Deserialize<RadarState>(json);
                }
                Invalidate();
            }
            catch (IOException)
            {
                // Lua replaces the state file atomically. Retry next tick.
            }
            catch (InvalidOperationException)
            {
                // Ignore a partial/invalid update and keep the previous frame.
            }
            catch (UnauthorizedAccessException exception)
            {
                ErrorLog.Write("Cannot read radar_state.json", exception);
            }
        }

        private void MoveOverGameWindow()
        {
            int targetX;
            int targetY;
            IntPtr gameWindow = IntPtr.Zero;
            Rect gameRectangle = new Rect();
            bool hasGameRectangle = false;
            bool usedClientRectangle = false;
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            UpdateGeometry(Screen.PrimaryScreen.Bounds.Height);
            targetX = workingArea.Right
                - _overlaySize
                - ScalePixels(ReferenceRightMargin);
            targetY = workingArea.Top + ScalePixels(ReferenceTopMargin);

            try
            {
                using (Process process = GameProcessFinder.FindNewest())
                {
                    if (process != null)
                    {
                        process.Refresh();
                        IntPtr handle = process.MainWindowHandle;
                        if (handle == IntPtr.Zero)
                        {
                            handle = FindLargestVisibleWindow(process.Id);
                        }
                        Rect rectangle;
                        if (handle != IntPtr.Zero
                            && GetWindowRect(handle, out rectangle))
                        {
                            gameWindow = handle;
                            Rect clientRectangle;
                            if (TryGetClientScreenRect(
                                handle,
                                out clientRectangle))
                            {
                                // The actual client area is the game's render
                                // surface in windowed, borderless, and
                                // fullscreen modes. Using it directly avoids
                                // stale FullscreenMode settings, window-frame
                                // offsets, and mixed DPI coordinate spaces.
                                rectangle = clientRectangle;
                                usedClientRectangle = true;
                            }
                            gameRectangle = rectangle;
                            hasGameRectangle = true;
                            int geometryHeight =
                                rectangle.Bottom - rectangle.Top;
                            UpdateGeometry(geometryHeight);
                            targetX = rectangle.Right
                                - _overlaySize
                                - ScalePixels(ReferenceRightMargin);
                            targetY = rectangle.Top
                                + ScalePixels(ReferenceTopMargin);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Game window detection failed; using desktop position", exception);
            }

            Rect overlayRectangle;
            bool alreadyPositioned =
                IsHandleCreated
                && GetWindowRect(Handle, out overlayRectangle)
                && overlayRectangle.Left == targetX
                && overlayRectangle.Top == targetY
                && overlayRectangle.Right - overlayRectangle.Left
                    == _overlaySize
                && overlayRectangle.Bottom - overlayRectangle.Top
                    == _overlaySize;
            if (!alreadyPositioned)
            {
                // Set the overlay in native screen pixels. WinForms can
                // otherwise apply an additional monitor-DPI conversion when
                // a borderless form is moved to a differently scaled monitor.
                if (!SetWindowPos(
                    Handle,
                    HwndTopmost,
                    targetX,
                    targetY,
                    _overlaySize,
                    _overlaySize,
                    SwpNoActivate | SwpShowWindow
                ))
                {
                    ErrorLog.WriteInfo(
                        "Overlay SetWindowPos failed: Win32 error " +
                        Marshal.GetLastWin32Error()
                    );
                }
            }

            if (hasGameRectangle)
            {
                LogGeometry(
                    gameWindow,
                    gameRectangle,
                    usedClientRectangle,
                    targetX,
                    targetY
                );
            }
        }

        private void LogGeometry(
            IntPtr gameWindow,
            Rect gameRectangle,
            bool usedClientRectangle,
            int targetX,
            int targetY)
        {
            Rect actualOverlay;
            bool hasActualOverlay =
                GetWindowRect(Handle, out actualOverlay);
            uint gameDpi = GetWindowDpi(gameWindow);
            uint overlayDpi = GetWindowDpi(Handle);
            string message = string.Format(
                CultureInfo.InvariantCulture,
                "Geometry: game={0},{1} {2}x{3}; source={4}; " +
                "gameDpi={5}; target={6},{7} {8}x{8}; " +
                "actual={9}; overlayDpi={10}; scale={11:0.####}",
                gameRectangle.Left,
                gameRectangle.Top,
                gameRectangle.Right - gameRectangle.Left,
                gameRectangle.Bottom - gameRectangle.Top,
                usedClientRectangle ? "client" : "window",
                gameDpi,
                targetX,
                targetY,
                _overlaySize,
                hasActualOverlay
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "{0},{1} {2}x{3}",
                        actualOverlay.Left,
                        actualOverlay.Top,
                        actualOverlay.Right - actualOverlay.Left,
                        actualOverlay.Bottom - actualOverlay.Top)
                    : "unavailable",
                overlayDpi,
                _displayScale
            );
            if (!string.Equals(
                message,
                _lastGeometryLog,
                StringComparison.Ordinal))
            {
                _lastGeometryLog = message;
                GeometryLog.Write(message);
            }
        }

        private static uint GetWindowDpi(IntPtr window)
        {
            try
            {
                return GetDpiForWindow(window);
            }
            catch (EntryPointNotFoundException)
            {
                return 0;
            }
            catch (DllNotFoundException)
            {
                return 0;
            }
        }

        private void UpdateGeometry(int windowHeight)
        {
            if (windowHeight <= 0)
            {
                windowHeight = (int)ReferenceWindowHeight;
            }

            _displayScale = windowHeight / ReferenceWindowHeight;
            _overlaySize = Math.Max(
                1,
                (int)Math.Round(ReferenceOverlaySize * _displayScale)
            );
        }

        private int ScalePixels(int referencePixels)
        {
            return (int)Math.Round(referencePixels * _displayScale);
        }

        private static bool TryGetClientScreenRect(
            IntPtr window,
            out Rect rectangle)
        {
            Rect client;
            NativePoint topLeft = new NativePoint();
            if (GetClientRect(window, out client)
                && ClientToScreen(window, ref topLeft))
            {
                int width = client.Right - client.Left;
                int height = client.Bottom - client.Top;
                if (width > 0 && height > 0)
                {
                    rectangle = new Rect
                    {
                        Left = topLeft.X,
                        Top = topLeft.Y,
                        Right = topLeft.X + width,
                        Bottom = topLeft.Y + height
                    };
                    return true;
                }
            }

            rectangle = new Rect();
            return false;
        }

        private static IntPtr FindLargestVisibleWindow(int processId)
        {
            IntPtr bestWindow = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint ownerProcessId;
                GetWindowThreadProcessId(window, out ownerProcessId);
                if (ownerProcessId != (uint)processId || !IsWindowVisible(window))
                {
                    return true;
                }

                Rect rectangle;
                if (!GetWindowRect(window, out rectangle))
                {
                    return true;
                }

                long width = rectangle.Right - rectangle.Left;
                long height = rectangle.Bottom - rectangle.Top;
                long area = width > 0 && height > 0 ? width * height : 0;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestWindow = window;
                }
                return true;
            }, IntPtr.Zero);
            return bestWindow;
        }

        private delegate bool EnumWindowsCallback(
            IntPtr window,
            IntPtr parameter
        );

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(
            EnumWindowsCallback callback,
            IntPtr parameter
        );

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId
        );

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(
            IntPtr windowHandle,
            out Rect rectangle
        );

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(
            IntPtr windowHandle,
            out Rect rectangle
        );

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(
            IntPtr windowHandle,
            ref NativePoint point
        );

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags
        );

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(
            IntPtr windowHandle
        );

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    internal sealed class RadarState
    {
        public bool enabled { get; set; }
        public double radius { get; set; }
        public List<RadarPoint> points { get; set; }
    }

    internal sealed class RadarPoint
    {
        public long saveId { get; set; }
        public double dx { get; set; }
        public double dy { get; set; }
    }

    internal sealed class TreasureSaveState
    {
        private const ulong SaveDatabaseOwnerPointerRva = 0x94DDB20;
        private readonly Dictionary<int, ulong> _opened =
            new Dictionary<int, ulong>();
        private DateTime _nextRefreshUtc;
        private string _lastDatabasePath;
        private DateTime _lastDatabaseWriteUtc;
        private string _lastKey;
        private string _lastError;
        private int _gameProcessId;

        public bool IsOpened(long saveId)
        {
            if (saveId <= 0)
            {
                return false;
            }

            int category = (int)(saveId / 64);
            int bit = (int)(saveId % 64);
            ulong field;
            return _opened.TryGetValue(category, out field)
                && (field & (1UL << bit)) != 0;
        }

        public void Refresh()
        {
            if (DateTime.UtcNow < _nextRefreshUtc)
            {
                return;
            }
            // Poll the file timestamp quickly so a newly opened treasure
            // disappears promptly. The encrypted DB is copied/read only when
            // its path, write time, or key actually changes.
            _nextRefreshUtc = DateTime.UtcNow.AddMilliseconds(250);

            try
            {
                using (Process game = GameProcessFinder.FindNewest())
                {
                    if (game == null)
                    {
                        ResetForGameProcess(0);
                        return;
                    }

                    if (game.Id != _gameProcessId)
                    {
                        ResetForGameProcess(game.Id);
                    }

                    string key = ReadDatabaseKey(game);
                    string databasePath = FindNewestSaveDatabase(game);
                    DateTime writeTime = File.GetLastWriteTimeUtc(databasePath);
                    if (databasePath == _lastDatabasePath
                        && writeTime == _lastDatabaseWriteUtc
                        && key == _lastKey)
                    {
                        return;
                    }

                    Dictionary<int, ulong> opened =
                        ReadOpenedTreasureBits(databasePath, key);
                    _opened.Clear();
                    foreach (KeyValuePair<int, ulong> pair in opened)
                    {
                        _opened[pair.Key] = pair.Value;
                    }
                    _lastDatabasePath = databasePath;
                    _lastDatabaseWriteUtc = writeTime;
                    _lastKey = key;
                    _lastError = null;
                }
            }
            catch (Exception exception)
            {
                string message = exception.GetType().FullName + ": "
                    + exception.Message;
                if (message != _lastError)
                {
                    _lastError = message;
                    ErrorLog.Write("Save-state refresh failed", exception);
                }
            }
        }

        private void ResetForGameProcess(int processId)
        {
            if (_gameProcessId == processId)
            {
                return;
            }

            _gameProcessId = processId;
            _opened.Clear();
            _lastDatabasePath = null;
            _lastDatabaseWriteUtc = DateTime.MinValue;
            _lastKey = null;
            _lastError = null;
        }

        private static string ReadDatabaseKey(Process game)
        {
            IntPtr process = NativeMethods.OpenProcess(
                0x0010 | 0x1000, false, game.Id);
            if (process == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "OpenProcess failed: " + Marshal.GetLastWin32Error());
            }

            try
            {
                ulong moduleBase = unchecked(
                    (ulong)game.MainModule.BaseAddress.ToInt64());
                ulong owner = ReadUInt64(
                    process, moduleBase + SaveDatabaseOwnerPointerRva);
                if (owner == 0)
                {
                    throw new InvalidOperationException(
                        "Save database owner is not ready.");
                }

                ulong keyPointer = ReadUInt64(process, owner + 0x120);
                int keyLength = ReadInt32(process, owner + 0x128);
                if (keyPointer == 0 || keyLength <= 1 || keyLength > 256)
                {
                    throw new InvalidOperationException(
                        "Save database key is not ready.");
                }

                byte[] bytes = ReadBytes(process, keyPointer, keyLength * 2);
                string key = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                if (key.Length == 0 || key.Any(
                    character => character < 0x20 || character > 0x7E))
                {
                    throw new InvalidOperationException(
                        "Save database key is not ready.");
                }
                return key;
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        private static string FindNewestSaveDatabase(Process game)
        {
            string win64 = Path.GetDirectoryName(game.MainModule.FileName);
            string saveRoot = Path.GetFullPath(Path.Combine(
                win64, "..", "..", "Saved", "SaveGames"));
            IEnumerable<string> candidates =
                Directory.GetFiles(
                    saveRoot, "*_Slot*.bak", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(
                    saveRoot, "*_Slot*.db", SearchOption.AllDirectories));
            string newest = candidates
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null)
            {
                throw new FileNotFoundException(
                    "No slot database was found.", saveRoot);
            }
            return newest;
        }

        private static Dictionary<int, ulong> ReadOpenedTreasureBits(
            string source, string key)
        {
            string temporaryDirectory = Path.Combine(
                Path.GetTempPath(), "DragonSwordTreasureRadar");
            Directory.CreateDirectory(temporaryDirectory);
            string temporary = Path.Combine(
                temporaryDirectory, Guid.NewGuid().ToString("N") + ".db");
            File.Copy(source, temporary, true);

            IntPtr database = IntPtr.Zero;
            try
            {
                int result = NativeMethods.sqlite3_open_v2(
                    Utf8(temporary), out database, 0x00000001, IntPtr.Zero);
                if (result != 0)
                {
                    throw SqliteError(database, result, IntPtr.Zero);
                }

                Dictionary<int, ulong> opened =
                    new Dictionary<int, ulong>();
                string escapedKey = key.Replace("'", "''");
                string sql =
                    "PRAGMA key = '" + escapedKey + "';"
                    + "PRAGMA cipher_compatibility = 4;"
                    + "SELECT CATEGORY,OPENED_BIT_FIELD "
                    + "FROM tb_treasure_box;";
                NativeMethods.ExecCallback callback = delegate(
                    IntPtr context, int count, IntPtr values, IntPtr names)
                {
                    if (count >= 2)
                    {
                        int category;
                        long signedField;
                        string categoryText = PointerString(
                            Marshal.ReadIntPtr(values, 0));
                        string fieldText = PointerString(
                            Marshal.ReadIntPtr(values, IntPtr.Size));
                        if (int.TryParse(categoryText, out category)
                            && long.TryParse(fieldText, out signedField))
                        {
                            opened[category] = unchecked((ulong)signedField);
                        }
                    }
                    return 0;
                };

                IntPtr error;
                result = NativeMethods.sqlite3_exec(
                    database, Utf8(sql), callback, IntPtr.Zero, out error);
                GC.KeepAlive(callback);
                if (result != 0)
                {
                    Exception exception = SqliteError(
                        database, result, error);
                    if (error != IntPtr.Zero)
                    {
                        NativeMethods.sqlite3_free(error);
                    }
                    throw exception;
                }
                return opened;
            }
            finally
            {
                if (database != IntPtr.Zero)
                {
                    NativeMethods.sqlite3_close_v2(database);
                }
                try { File.Delete(temporary); } catch { }
            }
        }

        private static Exception SqliteError(
            IntPtr database, int result, IntPtr error)
        {
            string message = error == IntPtr.Zero
                ? PointerString(NativeMethods.sqlite3_errmsg(database))
                : PointerString(error);
            return new InvalidOperationException(
                "SQLCipher error " + result + ": " + message);
        }

        private static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

        private static string PointerString(IntPtr pointer)
        {
            return pointer == IntPtr.Zero
                ? ""
                : Marshal.PtrToStringAnsi(pointer) ?? "";
        }

        private static ulong ReadUInt64(IntPtr process, ulong address)
        {
            return BitConverter.ToUInt64(ReadBytes(process, address, 8), 0);
        }

        private static int ReadInt32(IntPtr process, ulong address)
        {
            return BitConverter.ToInt32(ReadBytes(process, address, 4), 0);
        }

        private static byte[] ReadBytes(
            IntPtr process, ulong address, int size)
        {
            byte[] bytes = new byte[size];
            IntPtr read;
            if (!NativeMethods.ReadProcessMemory(
                    process,
                    new IntPtr(unchecked((long)address)),
                    bytes,
                    new IntPtr(size),
                    out read)
                || read.ToInt64() != size)
            {
                throw new InvalidOperationException(
                    "ReadProcessMemory failed at 0x"
                    + address.ToString("X") + ": "
                    + Marshal.GetLastWin32Error());
            }
            return bytes;
        }
    }

    internal static class NativeMethods
    {
        private const string SqlCipherLibrary = "e_sqlcipher.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ExecCallback(
            IntPtr context, int columnCount, IntPtr values, IntPtr names);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(
            uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, byte[] buffer,
            IntPtr size, out IntPtr bytesRead);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport(SqlCipherLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2(
            byte[] filename, out IntPtr database, int flags, IntPtr vfs);

        [DllImport(SqlCipherLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_exec(
            IntPtr database, byte[] sql, ExecCallback callback,
            IntPtr context, out IntPtr error);

        [DllImport(SqlCipherLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_errmsg(IntPtr database);

        [DllImport(SqlCipherLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close_v2(IntPtr database);

        [DllImport(SqlCipherLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3_free(IntPtr pointer);
    }
}
