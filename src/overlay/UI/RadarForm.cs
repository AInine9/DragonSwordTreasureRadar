using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DragonSwordTreasureRadar
{
    internal sealed class RadarForm : Form
    {
        private const float ReferenceWindowWidth = 2560f;
        private const float ReferenceWindowHeight = 1440f;
        private const int ReferenceOverlaySize = 360;
        private const int ReferenceRightMargin = 40;
        private const int ReferenceTopMargin = 37;
        private const float ReferenceRadarRadius = 170f;

        private readonly Timer _timer;
        private readonly string _statePath;
        private readonly JavaScriptSerializer _serializer;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly TreasureSaveState _saveState;
        private readonly WorldTreasureCatalog _worldTreasures;
        private List<WorldTreasure> _visibleWorldTreasures =
            new List<WorldTreasure>();

        private RadarState _state;
        private DateTime _lastStateWriteUtc;
        private DateTime _stateMissingSinceUtc;
        private DateTime _stateAccessFailureSinceUtc;
        private DateTime _nextStateAccessFailureLogUtc;
        private float _displayScale = 1f;
        private int _overlaySize = ReferenceOverlaySize;
        private string _lastGeometryLog;
        private string _lastSaveFilterLog;
        private DateTime _nextSaveFilterLogUtc;
        private DateTime _nextMaintenanceUtc;
        private int _lastSaveStateVersion = -1;
        private int _lastWorldTreasureCatalogVersion = -1;
        private int _lastWorldTreasureSaveVersion = -1;
        private int _gameProcessId;
        private bool _overlayVisible = true;

        public RadarForm()
        {
            _statePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "radar_state.json");
            _serializer = new JavaScriptSerializer();
            _saveState = new TreasureSaveState();
            _worldTreasures = new WorldTreasureCatalog();

            Rectangle primaryBounds = Screen.PrimaryScreen.Bounds;
            UpdateGeometry(
                primaryBounds.Width,
                primaryBounds.Height);
            ConfigureWindow();

            _trayMenu = CreateTrayMenu();
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "DragonSword Treasure Radar",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            _timer = new Timer
            {
                Interval = 250
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
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
                parameters.ExStyle |=
                    NativeMethods.WsExTransparent |
                    NativeMethods.WsExToolWindow |
                    NativeMethods.WsExLayered |
                    NativeMethods.WsExNoActivate;
                return parameters;
            }
        }

        protected override void OnFormClosed(
            FormClosedEventArgs eventArgs)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            base.OnFormClosed(eventArgs);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);

            RadarState state = _state;
            if (state == null
                || !state.enabled)
            {
                return;
            }

            eventArgs.Graphics.SmoothingMode =
                SmoothingMode.AntiAlias;
            if (string.Equals(
                state.mode,
                "world",
                StringComparison.Ordinal)
                && state.worldMap != null)
            {
                DrawWorldMap(eventArgs.Graphics, state.worldMap);
                return;
            }
            if (state.radius <= 0)
            {
                return;
            }

            float center = _overlaySize / 2f;
            float radarRadius =
                ReferenceRadarRadius * _displayScale;
            List<RadarPoint> points =
                (state.points ?? new List<RadarPoint>())
                .Where(point => !_saveState.IsOpened(point.saveId))
                .ToList();

            for (int index = points.Count - 1;
                index >= 0;
                index--)
            {
                DrawPoint(
                    eventArgs.Graphics,
                    points[index],
                    state.radius,
                    radarRadius,
                    center,
                    index == 0);
            }
        }

        private void DrawWorldMap(
            Graphics graphics,
            WorldMapState map)
        {
            if (!_saveState.HasLoadedSaveState
                || map.dimensions <= 0
                || map.uiSize <= 0
                || map.zoom <= 0
                || map.viewportWidth <= 0
                || map.viewportHeight <= 0
                || map.viewportScale <= 0)
            {
                return;
            }

            float windowScaleX =
                ClientSize.Width / (float)map.viewportWidth;
            float windowScaleY =
                ClientSize.Height / (float)map.viewportHeight;
            float coordinateScale = (float)map.viewportScale;
            float diameter = Math.Max(6f, 10f * _displayScale);
            float half = diameter / 2f;
            RectangleF clip = new RectangleF(
                0,
                0,
                ClientSize.Width,
                ClientSize.Height);
            GraphicsState saved = graphics.Save();
            graphics.SetClip(clip);

            using (Brush brush = new SolidBrush(
                Color.FromArgb(235, 90, 235, 255)))
            using (Pen outline = new Pen(
                Color.FromArgb(235, 5, 12, 22),
                Math.Max(1f, 2f * _displayScale)))
            {
                foreach (WorldTreasure treasure
                    in _visibleWorldTreasures)
                {
                    if (treasure.MapId != map.mapId)
                    {
                        continue;
                    }

                    double localX = map.playerMapX
                        + (treasure.X - map.playerWorldX)
                        / map.dimensions * map.uiSize;
                    double localY = map.playerMapY
                        + (treasure.Y - map.playerWorldY)
                        / map.dimensions * map.uiSize;
                    float x = (float)(
                        (map.left + localX * map.zoom)
                        * coordinateScale * windowScaleX);
                    float y = (float)(
                        (map.top + localY * map.zoom)
                        * coordinateScale * windowScaleY);
                    if (x < -half || x > ClientSize.Width + half
                        || y < -half || y > ClientSize.Height + half)
                    {
                        continue;
                    }

                    graphics.FillEllipse(
                        brush,
                        x - half,
                        y - half,
                        diameter,
                        diameter);
                    graphics.DrawEllipse(
                        outline,
                        x - half,
                        y - half,
                        diameter,
                        diameter);
                }
            }
            graphics.Restore(saved);
        }

        private void ConfigureWindow()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
        }

        private ContextMenuStrip CreateTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem exitItem = new ToolStripMenuItem(
                "Exit DragonSword Treasure Radar");
            exitItem.Click += delegate
            {
                _trayIcon.Visible = false;
                Close();
                Application.Exit();
            };
            menu.Items.Add(exitItem);
            return menu;
        }

        private void OnTimerTick(
            object sender,
            EventArgs eventArgs)
        {
            try
            {
                RefreshState();
                UpdateForegroundVisibility();
            }
            catch (Exception exception)
            {
                ErrorLog.Write(
                    "Radar refresh failed",
                    exception);
            }
        }

        private void DrawPoint(
            Graphics graphics,
            RadarPoint point,
            double stateRadius,
            float radarRadius,
            float center,
            bool nearest)
        {
            float x = center +
                (float)(point.dx / stateRadius * radarRadius);
            float y = center +
                (float)(point.dy / stateRadius * radarRadius);
            float diameter =
                (nearest ? 16f : 10f) * _displayScale;
            Color color = nearest
                ? Color.FromArgb(255, 255, 190, 45)
                : Color.FromArgb(235, 90, 235, 255);

            using (Brush brush = new SolidBrush(color))
            using (Pen outline = new Pen(
                Color.FromArgb(235, 5, 12, 22),
                Math.Max(1f, 2f * _displayScale)))
            {
                graphics.FillEllipse(
                    brush,
                    x - diameter / 2,
                    y - diameter / 2,
                    diameter,
                    diameter);
                graphics.DrawEllipse(
                    outline,
                    x - diameter / 2,
                    y - diameter / 2,
                    diameter,
                    diameter);
            }
        }

        private void RefreshState()
        {
            DateTime now = DateTime.UtcNow;
            bool maintenance = now >= _nextMaintenanceUtc;
            bool redraw = false;
            bool geometryChanged = false;
            if (maintenance)
            {
                _nextMaintenanceUtc = now.AddMilliseconds(250);
                _saveState.Refresh();
                _worldTreasures.Refresh();
                redraw = UpdateSaveStateVersion();
                redraw = UpdateWorldTreasureFilter() || redraw;
            }

            try
            {
                if (!File.Exists(_statePath))
                {
                    if (_stateMissingSinceUtc == DateTime.MinValue)
                    {
                        _stateMissingSinceUtc = now;
                    }
                    if (_state != null
                        && now - _stateMissingSinceUtc
                            >= TimeSpan.FromSeconds(1))
                    {
                        bool wasWorldMap = IsWorldMapMode();
                        _state = null;
                        _timer.Interval = 250;
                        if (wasWorldMap)
                        {
                            MoveOverGameWindow();
                        }
                        Invalidate();
                    }
                    if (maintenance)
                    {
                        LogSaveFilterStatus();
                    }
                    return;
                }
                _stateMissingSinceUtc = DateTime.MinValue;

                DateTime writeTime =
                    File.GetLastWriteTimeUtc(_statePath);
                if (writeTime != _lastStateWriteUtc)
                {
                    bool wasWorldMap = IsWorldMapMode();
                    RadarState loadedState =
                        _serializer.Deserialize<RadarState>(
                        File.ReadAllText(_statePath));
                    _state = loadedState;
                    _lastStateWriteUtc = writeTime;
                    _stateAccessFailureSinceUtc = DateTime.MinValue;
                    _timer.Interval = IsWorldMapMode()
                        ? 16
                        : 250;
                    geometryChanged =
                        wasWorldMap != IsWorldMapMode();
                    redraw = true;
                }
                if (redraw)
                {
                    if (geometryChanged)
                    {
                        MoveOverGameWindow();
                    }
                    Invalidate();
                }
            }
            catch (IOException)
            {
                // Lua replaces the state file atomically. Retry next tick.
            }
            catch (InvalidOperationException)
            {
                // Keep the previous frame during a partial JSON update.
            }
            catch (UnauthorizedAccessException exception)
            {
                if (_stateAccessFailureSinceUtc == DateTime.MinValue)
                {
                    _stateAccessFailureSinceUtc = now;
                }
                if (now - _stateAccessFailureSinceUtc
                        >= TimeSpan.FromSeconds(2)
                    && now >= _nextStateAccessFailureLogUtc)
                {
                    _nextStateAccessFailureLogUtc =
                        now.AddSeconds(30);
                    ErrorLog.Write(
                        "Cannot read radar_state.json",
                        exception);
                }
            }

            if (maintenance)
            {
                MoveOverGameWindow();
                LogSaveFilterStatus();
            }
        }

        private bool UpdateSaveStateVersion()
        {
            int version = _saveState.Version;
            if (version == _lastSaveStateVersion)
            {
                return false;
            }

            _lastSaveStateVersion = version;
            return true;
        }

        private bool UpdateWorldTreasureFilter()
        {
            int catalogVersion = _worldTreasures.Version;
            int saveVersion = _saveState.Version;
            if (catalogVersion == _lastWorldTreasureCatalogVersion
                && saveVersion == _lastWorldTreasureSaveVersion)
            {
                return false;
            }

            _lastWorldTreasureCatalogVersion = catalogVersion;
            _lastWorldTreasureSaveVersion = saveVersion;
            if (!_saveState.HasLoadedSaveState)
            {
                _visibleWorldTreasures =
                    new List<WorldTreasure>();
                return true;
            }

            _visibleWorldTreasures = _worldTreasures.Points
                .Where(treasure =>
                    !_saveState.IsOpened(treasure.SaveId))
                .ToList();
            return true;
        }

        private void LogSaveFilterStatus()
        {
            if (!DebugSettings.Enabled
                || DateTime.UtcNow < _nextSaveFilterLogUtc)
            {
                return;
            }

            _nextSaveFilterLogUtc =
                DateTime.UtcNow.AddSeconds(1);
            RadarState state = _state;
            List<RadarPoint> points =
                state == null || state.points == null
                    ? new List<RadarPoint>()
                    : state.points;
            int hidden = points.Count(
                point => _saveState.IsOpened(point.saveId));
            string message = string.Format(
                CultureInfo.InvariantCulture,
                "Save-filter status: gameProcessId={0}; " +
                "saveLoaded={1}; database={2}; openedBits={3}; " +
                "radarEnabled={4}; radarPoints={5}; hidden={6}; " +
                "visible={7}; lastError={8}",
                _saveState.GameProcessId,
                _saveState.HasLoadedSaveState,
                _saveState.DatabaseName,
                _saveState.OpenedBitCount,
                state != null && state.enabled,
                points.Count,
                hidden,
                points.Count - hidden,
                _saveState.LastErrorSummary);

            if (message != _lastSaveFilterLog)
            {
                _lastSaveFilterLog = message;
                ErrorLog.WriteDebug(message);
            }
        }

        private void MoveOverGameWindow()
        {
            Rectangle primaryBounds = Screen.PrimaryScreen.Bounds;
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            UpdateGeometry(
                primaryBounds.Width,
                primaryBounds.Height);

            int targetX = workingArea.Right
                - _overlaySize
                - ScalePixels(ReferenceRightMargin);
            int targetY = workingArea.Top
                + ScalePixels(ReferenceTopMargin);
            int targetWidth = _overlaySize;
            int targetHeight = _overlaySize;
            IntPtr gameWindow = IntPtr.Zero;
            NativeRect gameRectangle = new NativeRect();
            bool hasGameRectangle = false;
            bool usedClientRectangle = false;
            _gameProcessId = 0;

            try
            {
                using (Process process =
                    GameProcessFinder.FindNewest())
                {
                    if (process != null)
                    {
                        _gameProcessId = process.Id;
                        process.Refresh();
                        gameWindow = process.MainWindowHandle;
                        if (gameWindow == IntPtr.Zero)
                        {
                            gameWindow = FindLargestVisibleWindow(
                                process.Id);
                        }

                        NativeRect rectangle;
                        if (gameWindow != IntPtr.Zero
                            && NativeMethods.GetWindowRect(
                                gameWindow,
                                out rectangle))
                        {
                            NativeRect clientRectangle;
                            if (TryGetClientScreenRect(
                                gameWindow,
                                out clientRectangle))
                            {
                                rectangle = clientRectangle;
                                usedClientRectangle = true;
                            }

                            gameRectangle = rectangle;
                            hasGameRectangle = true;
                            UpdateGeometry(
                                rectangle.Width,
                                rectangle.Height);
                            targetX = rectangle.Right
                                - _overlaySize
                                - ScalePixels(
                                    ReferenceRightMargin);
                            targetY = rectangle.Top
                                + ScalePixels(
                                    ReferenceTopMargin);
                            if (IsWorldMapMode())
                            {
                                targetX = rectangle.Left;
                                targetY = rectangle.Top;
                                targetWidth = rectangle.Width;
                                targetHeight = rectangle.Height;
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                ErrorLog.Write(
                    "Game window detection failed; " +
                    "using desktop position",
                    exception);
            }

            if (IsWorldMapMode() && !hasGameRectangle)
            {
                targetX = primaryBounds.Left;
                targetY = primaryBounds.Top;
                targetWidth = primaryBounds.Width;
                targetHeight = primaryBounds.Height;
            }

            PositionOverlay(
                targetX,
                targetY,
                targetWidth,
                targetHeight);
            if (hasGameRectangle)
            {
                LogGeometry(
                    gameWindow,
                    gameRectangle,
                    usedClientRectangle,
                    targetX,
                    targetY,
                    targetWidth,
                    targetHeight);
            }
        }

        private bool IsWorldMapMode()
        {
            return _state != null
                && _state.enabled
                && _state.worldMap != null
                && string.Equals(
                    _state.mode,
                    "world",
                    StringComparison.Ordinal);
        }

        private void PositionOverlay(
            int targetX,
            int targetY,
            int targetWidth,
            int targetHeight)
        {
            NativeRect current;
            bool alreadyPositioned =
                IsHandleCreated
                && NativeMethods.GetWindowRect(Handle, out current)
                && current.Left == targetX
                && current.Top == targetY
                && current.Width == targetWidth
                && current.Height == targetHeight;
            if (alreadyPositioned)
            {
                return;
            }

            if (!NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopmost,
                targetX,
                targetY,
                targetWidth,
                targetHeight,
                NativeMethods.SwpNoActivate))
            {
                ErrorLog.WriteMessage(
                    "Overlay SetWindowPos failed: Win32 error " +
                    Marshal.GetLastWin32Error());
            }
        }

        private void UpdateForegroundVisibility()
        {
            bool shouldShow = false;
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (_gameProcessId > 0 && foreground != IntPtr.Zero)
            {
                uint foregroundProcessId;
                NativeMethods.GetWindowThreadProcessId(
                    foreground,
                    out foregroundProcessId);
                shouldShow =
                    foregroundProcessId == (uint)_gameProcessId;
            }
            if (shouldShow == _overlayVisible || !IsHandleCreated)
            {
                return;
            }

            NativeMethods.ShowWindow(
                Handle,
                shouldShow
                    ? NativeMethods.SwShowNoActivate
                    : NativeMethods.SwHide);
            _overlayVisible = shouldShow;
        }

        private void LogGeometry(
            IntPtr gameWindow,
            NativeRect gameRectangle,
            bool usedClientRectangle,
            int targetX,
            int targetY,
            int targetWidth,
            int targetHeight)
        {
            if (!DebugSettings.Enabled)
            {
                return;
            }

            NativeRect actualOverlay;
            bool hasActualOverlay =
                NativeMethods.GetWindowRect(
                    Handle,
                    out actualOverlay);
            string message = string.Format(
                CultureInfo.InvariantCulture,
                "Geometry: game={0},{1} {2}x{3}; source={4}; " +
                "gameDpi={5}; target={6},{7} {8}x{9}; " +
                "actual={10}; overlayDpi={11}; scale={12:0.####}",
                gameRectangle.Left,
                gameRectangle.Top,
                gameRectangle.Width,
                gameRectangle.Height,
                usedClientRectangle ? "client" : "window",
                GetWindowDpi(gameWindow),
                targetX,
                targetY,
                targetWidth,
                targetHeight,
                hasActualOverlay
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "{0},{1} {2}x{3}",
                        actualOverlay.Left,
                        actualOverlay.Top,
                        actualOverlay.Width,
                        actualOverlay.Height)
                    : "unavailable",
                GetWindowDpi(Handle),
                _displayScale);
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
                return NativeMethods.GetDpiForWindow(window);
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

        private void UpdateGeometry(
            int windowWidth,
            int windowHeight)
        {
            if (windowWidth <= 0)
            {
                windowWidth = (int)ReferenceWindowWidth;
            }
            if (windowHeight <= 0)
            {
                windowHeight = (int)ReferenceWindowHeight;
            }

            _displayScale = Math.Min(
                windowWidth / ReferenceWindowWidth,
                windowHeight / ReferenceWindowHeight);
            _overlaySize = Math.Max(
                1,
                (int)Math.Round(
                    ReferenceOverlaySize * _displayScale));
        }

        private int ScalePixels(int referencePixels)
        {
            return (int)Math.Round(
                referencePixels * _displayScale);
        }

        private static bool TryGetClientScreenRect(
            IntPtr window,
            out NativeRect rectangle)
        {
            NativeRect client;
            NativePoint topLeft = new NativePoint();
            if (NativeMethods.GetClientRect(window, out client)
                && NativeMethods.ClientToScreen(
                    window,
                    ref topLeft)
                && client.Width > 0
                && client.Height > 0)
            {
                rectangle = new NativeRect
                {
                    Left = topLeft.X,
                    Top = topLeft.Y,
                    Right = topLeft.X + client.Width,
                    Bottom = topLeft.Y + client.Height
                };
                return true;
            }

            rectangle = new NativeRect();
            return false;
        }

        private static IntPtr FindLargestVisibleWindow(
            int processId)
        {
            IntPtr bestWindow = IntPtr.Zero;
            long bestArea = 0;

            NativeMethods.EnumWindows(delegate(
                IntPtr window,
                IntPtr parameter)
            {
                uint ownerProcessId;
                NativeMethods.GetWindowThreadProcessId(
                    window,
                    out ownerProcessId);
                if (ownerProcessId != (uint)processId
                    || !NativeMethods.IsWindowVisible(window))
                {
                    return true;
                }

                NativeRect rectangle;
                if (!NativeMethods.GetWindowRect(
                    window,
                    out rectangle))
                {
                    return true;
                }

                long area =
                    rectangle.Width > 0 && rectangle.Height > 0
                        ? (long)rectangle.Width * rectangle.Height
                        : 0;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestWindow = window;
                }
                return true;
            }, IntPtr.Zero);

            return bestWindow;
        }
    }
}
