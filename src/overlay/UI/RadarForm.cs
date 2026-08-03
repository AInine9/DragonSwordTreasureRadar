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

        // Height indicators use a continuous 180-degree scale. The player
        // actor origin sits above the treasure reference point, so the
        // player Z value is shifted before comparison. Near-equal heights
        // keep a horizontal pointer instead of hiding the indicator.
        private const double ComparablePlayerZOffset = -100.0;
        private const double HeightIndicatorDeadZone = 100.0;
        private const double HeightIndicatorSensitivity = 2500.0;
        private const double HeightIndicatorMaximumAngleDegrees = 85.0;

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
            bool showDebugCoordinates = DebugSettings.Enabled;
            float textScale = NormalizeTextScale(state.textScale);
            if (string.Equals(
                state.mode,
                "world",
                StringComparison.Ordinal)
                && state.worldMap != null)
            {
                DrawWorldMap(
                    eventArgs.Graphics,
                    state.worldMap,
                    state.showHeight,
                    state.showTreasureTypes,
                    showDebugCoordinates,
                    textScale,
                    state.playerZ,
                    state.hasPlayerZ);
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
                    index == 0,
                    state.showHeight,
                    state.showTreasureTypes,
                    showDebugCoordinates,
                    textScale,
                    state.playerZ,
                    state.hasPlayerZ);
            }
        }

        private void DrawWorldMap(
            Graphics graphics,
            WorldMapState map,
            bool showHeight,
            bool showTreasureTypes,
            bool showDebugCoordinates,
            float textScale,
            double playerZ,
            bool hasPlayerZ)
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

            bool showLabel =
                showDebugCoordinates || showTreasureTypes;

            float windowScaleX =
                ClientSize.Width / (float)map.viewportWidth;
            float windowScaleY =
                ClientSize.Height / (float)map.viewportHeight;
            float coordinateScale = (float)map.viewportScale;
            float normalDiameter = Math.Max(
                6f,
                10f * _displayScale);
            float nearestDiameter = Math.Max(
                10f,
                16f * _displayScale);
            RectangleF clip = new RectangleF(
                0,
                0,
                ClientSize.Width,
                ClientSize.Height);
            GraphicsState saved = graphics.Save();
            graphics.SetClip(clip);

            WorldTreasure nearestTreasure = null;
            float nearestX = 0f;
            float nearestY = 0f;
            double nearestDistanceSquared = double.MaxValue;

            foreach (WorldTreasure treasure
                in _visibleWorldTreasures)
            {
                if (treasure.MapId != map.mapId)
                {
                    continue;
                }

                float projectedX;
                float projectedY;
                if (!TryProjectWorldTreasure(
                    treasure,
                    map,
                    coordinateScale,
                    windowScaleX,
                    windowScaleY,
                    normalDiameter / 2f,
                    out projectedX,
                    out projectedY))
                {
                    continue;
                }

                double deltaX = treasure.X - map.playerWorldX;
                double deltaY = treasure.Y - map.playerWorldY;
                double distanceSquared =
                    deltaX * deltaX + deltaY * deltaY;
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestTreasure = treasure;
                    nearestX = projectedX;
                    nearestY = projectedY;
                }
            }

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

                    float x;
                    float y;
                    if (!TryProjectWorldTreasure(
                        treasure,
                        map,
                        coordinateScale,
                        windowScaleX,
                        windowScaleY,
                        nearestDiameter / 2f,
                        out x,
                        out y))
                    {
                        continue;
                    }

                    bool nearest = object.ReferenceEquals(
                        treasure,
                        nearestTreasure);
                    float diameter = nearest
                        ? nearestDiameter
                        : normalDiameter;
                    Color color = GetTreasureColor(treasure);

                    DrawTreasureMarker(
                        graphics,
                        x,
                        y,
                        diameter,
                        color,
                        nearest,
                        outline);
                }
            }

            if (nearestTreasure != null)
            {
                Color nearestColor =
                    GetTreasureColor(nearestTreasure);
                if (showHeight)
                {
                    DrawHeightIndicator(
                        graphics,
                        nearestX,
                        nearestY,
                        nearestDiameter,
                        playerZ,
                        hasPlayerZ,
                        nearestTreasure.Z,
                        nearestTreasure.HasZ,
                        nearestColor);
                }
                if (showLabel)
                {
                    DrawNearestLabel(
                        graphics,
                        nearestX,
                        nearestY,
                        nearestDiameter,
                        textScale,
                        showDebugCoordinates,
                        showTreasureTypes,
                        playerZ,
                        hasPlayerZ,
                        nearestTreasure.Z,
                        nearestTreasure.HasZ,
                        nearestTreasure.SaveId,
                        nearestTreasure);
                }
            }

            graphics.Restore(saved);
        }

        private bool TryProjectWorldTreasure(
            WorldTreasure treasure,
            WorldMapState map,
            float coordinateScale,
            float windowScaleX,
            float windowScaleY,
            float margin,
            out float x,
            out float y)
        {
            double localX = map.playerMapX
                + (treasure.X - map.playerWorldX)
                / map.dimensions * map.uiSize;
            double localY = map.playerMapY
                + (treasure.Y - map.playerWorldY)
                / map.dimensions * map.uiSize;
            x = (float)(
                (map.left + localX * map.zoom)
                * coordinateScale * windowScaleX);
            y = (float)(
                (map.top + localY * map.zoom)
                * coordinateScale * windowScaleY);

            return x >= -margin
                && x <= ClientSize.Width + margin
                && y >= -margin
                && y <= ClientSize.Height + margin;
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
            bool nearest,
            bool showHeight,
            bool showTreasureTypes,
            bool showDebugCoordinates,
            float textScale,
            double playerZ,
            bool hasPlayerZ)
        {
            float x = center +
                (float)(point.dx / stateRadius * radarRadius);
            float y = center +
                (float)(point.dy / stateRadius * radarRadius);
            float diameter =
                (nearest ? 16f : 10f) * _displayScale;
            WorldTreasure metadata =
                _worldTreasures.FindBySaveIdAndCoordinates(
                    point.saveId,
                    point.x,
                    point.y);
            Color color = GetTreasureColor(metadata);

            using (Pen outline = new Pen(
                Color.FromArgb(235, 5, 12, 22),
                Math.Max(1f, 2f * _displayScale)))
            {
                DrawTreasureMarker(
                    graphics,
                    x,
                    y,
                    diameter,
                    color,
                    nearest,
                    outline);
            }

            if (nearest)
            {
                if (showHeight)
                {
                    DrawHeightIndicator(
                        graphics,
                        x,
                        y,
                        diameter,
                        playerZ,
                        hasPlayerZ,
                        point.z,
                        point.hasZ,
                        color);
                }
                if (showDebugCoordinates || showTreasureTypes)
                {
                    DrawNearestLabel(
                        graphics,
                        x,
                        y,
                        diameter,
                        textScale,
                        showDebugCoordinates,
                        showTreasureTypes,
                        playerZ,
                        hasPlayerZ,
                        point.z,
                        point.hasZ,
                        point.saveId,
                        metadata);
                }
            }
        }

        // The nearest marker keeps its acquisition-type color while using
        // the larger nearest-marker diameter for visibility.
        private void DrawTreasureMarker(
            Graphics graphics,
            float centerX,
            float centerY,
            float diameter,
            Color innerColor,
            bool nearest,
            Pen outline)
        {
            float half = diameter / 2f;

            if (nearest)
            {
                using (Brush highlightBrush = new SolidBrush(
                    innerColor))
                {
                    graphics.FillEllipse(
                        highlightBrush,
                        centerX - half,
                        centerY - half,
                        diameter,
                        diameter);
                }
                graphics.DrawEllipse(
                    outline,
                    centerX - half,
                    centerY - half,
                    diameter,
                    diameter);
                return;
            }

            using (Brush brush = new SolidBrush(innerColor))
            {
                graphics.FillEllipse(
                    brush,
                    centerX - half,
                    centerY - half,
                    diameter,
                    diameter);
            }
            graphics.DrawEllipse(
                outline,
                centerX - half,
                centerY - half,
                diameter,
                diameter);
        }

        // show_height controls the normal height pointer. Exact Z values are
        // displayed only while debug_logging is enabled, and treasure type
        // labels remain independently configurable.
        private void DrawHeightIndicator(
            Graphics graphics,
            float anchorX,
            float anchorY,
            float markerDiameter,
            double playerZ,
            bool hasPlayerZ,
            double treasureZ,
            bool hasTreasureZ,
            Color color)
        {
            if (!hasPlayerZ || !hasTreasureZ)
            {
                return;
            }

            double comparablePlayerZ = GetComparablePlayerZ(playerZ);
            double deltaZ = treasureZ - comparablePlayerZ;
            if (Math.Abs(deltaZ) <= HeightIndicatorDeadZone)
            {
                deltaZ = 0.0;
            }

            // The pointer rotates through the right semicircle: 12 o'clock
            // means above, 3 o'clock means near the same height, and
            // 6 o'clock means below. It is anchored just left of the marker.
            double pointerAngle = -Math.Atan(
                deltaZ / HeightIndicatorSensitivity);
            double maximumAngle =
                HeightIndicatorMaximumAngleDegrees
                * Math.PI / 180.0;
            pointerAngle = Math.Max(
                -maximumAngle,
                Math.Min(maximumAngle, pointerAngle));

            float directionX = (float)Math.Cos(pointerAngle);
            float directionY = (float)Math.Sin(pointerAngle);
            float perpendicularX = -directionY;
            float perpendicularY = directionX;

            // Keep the pointer short, thick, and close to the treasure marker.
            float indicatorLength = Math.Min(
                36f,
                Math.Max(20f, 24f * _displayScale));
            float markerGap = Math.Min(
                3f,
                Math.Max(1.5f, 2f * _displayScale));
            float pointerCenterX = anchorX
                - markerDiameter / 2f
                - markerGap
                - indicatorLength / 2f;
            float pointerCenterY = anchorY;
            float halfLength = indicatorLength / 2f;
            float startX = pointerCenterX - directionX * halfLength;
            float startY = pointerCenterY - directionY * halfLength;
            float tipX = pointerCenterX + directionX * halfLength;
            float tipY = pointerCenterY + directionY * halfLength;

            float headLength = Math.Min(
                15f,
                Math.Max(8f, 10f * _displayScale));
            float headHalfWidth = Math.Min(
                8f,
                Math.Max(4f, 5f * _displayScale));
            float baseX = tipX - directionX * headLength;
            float baseY = tipY - directionY * headLength;

            float outlineWidth = Math.Min(
                11f,
                Math.Max(6f, 8f * _displayScale));
            float innerWidth = Math.Min(
                8.5f,
                Math.Max(4.5f, 6f * _displayScale));
            Color outlineColor = Color.FromArgb(235, 5, 12, 22);
            Color indicatorColor = Color.FromArgb(
                255,
                color.R,
                color.G,
                color.B);

            using (Pen outlinePen = new Pen(
                outlineColor,
                outlineWidth))
            using (Pen indicatorPen = new Pen(
                indicatorColor,
                innerWidth))
            {
                outlinePen.StartCap = LineCap.Round;
                outlinePen.EndCap = LineCap.Round;
                indicatorPen.StartCap = LineCap.Round;
                indicatorPen.EndCap = LineCap.Round;
                graphics.DrawLine(
                    outlinePen,
                    startX,
                    startY,
                    baseX,
                    baseY);
                graphics.DrawLine(
                    indicatorPen,
                    startX,
                    startY,
                    baseX,
                    baseY);
            }

            float headOutlineExpansion = Math.Min(
                3f,
                Math.Max(2f, 2f * _displayScale));
            PointF[] outlineHead =
            {
                new PointF(tipX, tipY),
                new PointF(
                    baseX + perpendicularX
                        * (headHalfWidth + headOutlineExpansion),
                    baseY + perpendicularY
                        * (headHalfWidth + headOutlineExpansion)),
                new PointF(
                    baseX - perpendicularX
                        * (headHalfWidth + headOutlineExpansion),
                    baseY - perpendicularY
                        * (headHalfWidth + headOutlineExpansion))
            };
            PointF[] colorHead =
            {
                new PointF(tipX, tipY),
                new PointF(
                    baseX + perpendicularX * headHalfWidth,
                    baseY + perpendicularY * headHalfWidth),
                new PointF(
                    baseX - perpendicularX * headHalfWidth,
                    baseY - perpendicularY * headHalfWidth)
            };
            using (Brush outlineBrush = new SolidBrush(outlineColor))
            using (Brush indicatorBrush = new SolidBrush(indicatorColor))
            {
                graphics.FillPolygon(outlineBrush, outlineHead);
                graphics.FillPolygon(indicatorBrush, colorHead);
            }
        }

        private void DrawNearestLabel(
            Graphics graphics,
            float anchorX,
            float anchorY,
            float markerDiameter,
            float textScale,
            bool showDebugCoordinates,
            bool showTreasureTypes,
            double playerZ,
            bool hasPlayerZ,
            double treasureZ,
            bool hasTreasureZ,
            long saveId,
            WorldTreasure metadata)
        {
            string coordinateText = null;
            if (showDebugCoordinates)
            {
                string playerText = hasPlayerZ
                    ? GetComparablePlayerZ(playerZ).ToString(
                        "0",
                        CultureInfo.InvariantCulture)
                    : "?";
                string treasureText = hasTreasureZ
                    ? treasureZ.ToString(
                        "0",
                        CultureInfo.InvariantCulture)
                    : "?";
                coordinateText = string.Format(
                    CultureInfo.InvariantCulture,
                    "({0}, {1})",
                    playerText,
                    treasureText);
            }

            string typeText = null;
            if (showTreasureTypes)
            {
                typeText = metadata == null
                    ? TreasureIdentity.GetDebugName(null, saveId)
                    : metadata.DebugName;
            }

            if (String.IsNullOrEmpty(typeText)
                && String.IsNullOrEmpty(coordinateText))
            {
                return;
            }

            Color baseColor = GetTreasureColor(metadata);
            Color typeColor = Color.FromArgb(
                210,
                baseColor.R,
                baseColor.G,
                baseColor.B);
            Color coordinateColor = Color.FromArgb(
                200,
                baseColor.R,
                baseColor.G,
                baseColor.B);

            // Pixel units prevent Windows DPI scaling from changing label
            // size unexpectedly. text_scale provides one user-facing control
            // for both treasure type and debug coordinate labels.
            float baseFontSize = Math.Min(
                26f,
                Math.Max(16f, 18f * _displayScale));
            float fontSize = baseFontSize * textScale;
            float verticalGap = Math.Min(
                8f,
                Math.Max(4f, 5f * _displayScale));
            float lineGap = Math.Min(
                4f,
                Math.Max(1f, 2f * _displayScale));

            using (Font font = new Font(
                SystemFonts.MessageBoxFont.FontFamily,
                fontSize,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            {
                SizeF typeSize = String.IsNullOrEmpty(typeText)
                    ? SizeF.Empty
                    : graphics.MeasureString(typeText, font);
                SizeF coordinateSize =
                    String.IsNullOrEmpty(coordinateText)
                        ? SizeF.Empty
                        : graphics.MeasureString(
                            coordinateText,
                            font);
                float blockHeight = typeSize.Height
                    + coordinateSize.Height;
                if (!typeSize.IsEmpty && !coordinateSize.IsEmpty)
                {
                    blockHeight += lineGap;
                }

                float top = anchorY
                    + markerDiameter / 2f
                    + verticalGap;
                if (top + blockHeight > ClientSize.Height)
                {
                    top = anchorY
                        - markerDiameter / 2f
                        - verticalGap
                        - blockHeight;
                }
                if (top < 0f)
                {
                    top = 0f;
                }

                float outlineOffset = Math.Min(
                    2.5f,
                    Math.Max(1.25f, 1.5f * _displayScale));
                if (!String.IsNullOrEmpty(typeText))
                {
                    DrawOutlinedLabelLine(
                        graphics,
                        typeText,
                        font,
                        typeColor,
                        anchorX,
                        top,
                        typeSize,
                        outlineOffset);
                    top += typeSize.Height;
                    if (!String.IsNullOrEmpty(coordinateText))
                    {
                        top += lineGap;
                    }
                }
                if (!String.IsNullOrEmpty(coordinateText))
                {
                    DrawOutlinedLabelLine(
                        graphics,
                        coordinateText,
                        font,
                        coordinateColor,
                        anchorX,
                        top,
                        coordinateSize,
                        outlineOffset);
                }
            }
        }

        private void DrawOutlinedLabelLine(
            Graphics graphics,
            string text,
            Font font,
            Color textColor,
            float centerX,
            float top,
            SizeF measured,
            float outlineOffset)
        {
            float left = centerX - measured.Width / 2f;
            if (left < 0f)
            {
                left = 0f;
            }
            if (left + measured.Width > ClientSize.Width)
            {
                left = ClientSize.Width - measured.Width;
            }

            using (Brush outlineBrush = new SolidBrush(
                Color.FromArgb(220, 0, 0, 0)))
            using (Brush textBrush = new SolidBrush(textColor))
            {
                graphics.DrawString(
                    text,
                    font,
                    outlineBrush,
                    left - outlineOffset,
                    top);
                graphics.DrawString(
                    text,
                    font,
                    outlineBrush,
                    left + outlineOffset,
                    top);
                graphics.DrawString(
                    text,
                    font,
                    outlineBrush,
                    left,
                    top - outlineOffset);
                graphics.DrawString(
                    text,
                    font,
                    outlineBrush,
                    left,
                    top + outlineOffset);
                graphics.DrawString(
                    text,
                    font,
                    textBrush,
                    left,
                    top);
            }
        }

        private static float NormalizeTextScale(double configuredScale)
        {
            if (Double.IsNaN(configuredScale)
                || Double.IsInfinity(configuredScale)
                || configuredScale <= 0)
            {
                return 1f;
            }

            return (float)Math.Max(
                0.5,
                Math.Min(2.0, configuredScale));
        }

        // Type colors intentionally describe acquisition mechanics, not rarity.
        // MiniGame is green, Map is orange, and all other types are white.
        private static Color GetTreasureColor(
            WorldTreasure treasure)
        {
            TreasureKind kind = treasure == null
                ? TreasureKind.Other
                : treasure.Kind;
            if (kind == TreasureKind.MiniGame)
            {
                return Color.FromArgb(255, 85, 235, 115);
            }
            if (kind == TreasureKind.Map)
            {
                return Color.FromArgb(255, 255, 165, 35);
            }
            return Color.FromArgb(245, 255, 255, 255);
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
                DateTime.UtcNow.AddSeconds(2);
            RadarState state = _state;
            List<RadarPoint> points =
                state == null || state.points == null
                    ? new List<RadarPoint>()
                    : state.points;
            int hidden = points.Count(
                point => _saveState.IsOpened(point.saveId));
            string mode = state == null
                ? "none"
                : state.mode ?? "minimap";
            string playerZ = state != null
                && state.hasPlayerZ
                ? GetComparablePlayerZ(state.playerZ).ToString(
                    "0",
                    CultureInfo.InvariantCulture)
                : "?";
            string details = IsWorldMapMode()
                ? BuildWorldMapDebugDetails(state)
                : BuildRadarDebugDetails(points);
            string message = string.Format(
                CultureInfo.InvariantCulture,
                "Treasure debug snapshot: mode={0}; " +
                "gameProcessId={1}; saveLoaded={2}; " +
                "database={3}; databaseWrite={4}; " +
                "openedBits={5}; radarEnabled={6}; " +
                "playerZ={7}; radarPoints={8}; hidden={9}; " +
                "visible={10}; lastError={11}",
                mode,
                _saveState.GameProcessId,
                _saveState.HasLoadedSaveState,
                _saveState.DatabaseName,
                _saveState.DatabaseWriteSummary,
                _saveState.OpenedBitCount,
                state != null && state.enabled,
                playerZ,
                points.Count,
                hidden,
                points.Count - hidden,
                _saveState.LastErrorSummary)
                + Environment.NewLine
                + details;

            if (message != _lastSaveFilterLog)
            {
                _lastSaveFilterLog = message;
                ErrorLog.WriteDebug(message);
            }
        }

        private string BuildRadarDebugDetails(
            IList<RadarPoint> points)
        {
            if (points == null || points.Count == 0)
            {
                return "Nearby treasure details: none";
            }

            List<string> rows = new List<string>();
            int count = Math.Min(points.Count, 12);
            for (int index = 0; index < count; index++)
            {
                RadarPoint point = points[index];
                WorldTreasure metadata =
                    _worldTreasures.FindBySaveIdAndCoordinates(
                        point.saveId,
                        point.x,
                        point.y);
                double horizontal = Math.Sqrt(
                    point.dx * point.dx +
                    point.dy * point.dy);
                rows.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "  [{0}] name={1}; id={2}; uidName={3}; groupId={4}; " +
                    "x={5:0}; y={6:0}; z={7}; " +
                    "dxy={8:0}({9:0.0}m); dz={10}; {11}; overlaps={12}",
                    index,
                    metadata == null
                        ? TreasureIdentity.GetDebugName(
                            null,
                            point.saveId)
                        : metadata.DebugName,
                    point.saveId,
                    metadata == null
                        || String.IsNullOrWhiteSpace(metadata.UidName)
                        ? "(missing)"
                        : metadata.UidName,
                    metadata == null
                        ? 0
                        : metadata.GroupId,
                    point.x,
                    point.y,
                    point.hasZ
                        ? point.z.ToString(
                            "0",
                            CultureInfo.InvariantCulture)
                        : "?",
                    horizontal,
                    horizontal / 100.0,
                    GetVerticalDeltaText(
                        _state,
                        point.z,
                        point.hasZ),
                    _saveState.Describe(point.saveId),
                    FindRadarOverlapSummary(
                        point,
                        points)));
            }

            return "Nearby treasure details:"
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    rows.ToArray());
        }

        private string BuildWorldMapDebugDetails(
            RadarState state)
        {
            if (state == null || state.worldMap == null)
            {
                return "World-map treasure details: none";
            }

            WorldMapState map = state.worldMap;
            List<WorldTreasure> nearest =
                _worldTreasures.Points
                    .Where(treasure =>
                        treasure.MapId == map.mapId)
                    .OrderBy(treasure =>
                    {
                        double deltaX =
                            treasure.X - map.playerWorldX;
                        double deltaY =
                            treasure.Y - map.playerWorldY;
                        return deltaX * deltaX +
                            deltaY * deltaY;
                    })
                    .Take(12)
                    .ToList();

            if (nearest.Count == 0)
            {
                return "World-map treasure details: none";
            }

            List<string> rows = new List<string>();
            for (int index = 0;
                index < nearest.Count;
                index++)
            {
                WorldTreasure treasure = nearest[index];
                double deltaX =
                    treasure.X - map.playerWorldX;
                double deltaY =
                    treasure.Y - map.playerWorldY;
                double horizontal = Math.Sqrt(
                    deltaX * deltaX +
                    deltaY * deltaY);
                rows.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "  [{0}] name={1}; id={2}; uidName={3}; groupId={4}; " +
                    "x={5:0}; y={6:0}; z={7}; " +
                    "dxy={8:0}({9:0.0}m); dz={10}; {11}; overlaps={12}",
                    index,
                    treasure.DebugName,
                    treasure.SaveId,
                    treasure.UidName ?? "(missing)",
                    treasure.GroupId,
                    treasure.X,
                    treasure.Y,
                    treasure.HasZ
                        ? treasure.Z.ToString(
                            "0",
                            CultureInfo.InvariantCulture)
                        : "?",
                    horizontal,
                    horizontal / 100.0,
                    GetVerticalDeltaText(
                        state,
                        treasure.Z,
                        treasure.HasZ),
                    _saveState.Describe(
                        treasure.SaveId),
                    FindWorldOverlapSummary(
                        treasure,
                        nearest)));
            }

            return "World-map treasure details:"
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    rows.ToArray());
        }

        private static string GetVerticalDeltaText(
            RadarState state,
            double treasureZ,
            bool hasTreasureZ)
        {
            if (state == null
                || !state.hasPlayerZ
                || !hasTreasureZ)
            {
                return "?";
            }

            return (treasureZ
                - GetComparablePlayerZ(state.playerZ)).ToString(
                "0",
                CultureInfo.InvariantCulture);
        }

        private static double GetComparablePlayerZ(double playerZ)
        {
            return playerZ + ComparablePlayerZOffset;
        }

        private static string FindRadarOverlapSummary(
            RadarPoint source,
            IEnumerable<RadarPoint> points)
        {
            string[] overlaps = points
                .Where(candidate =>
                    !object.ReferenceEquals(candidate, source)
                    && CandidateIsClose(
                        source.x,
                        source.y,
                        candidate.x,
                        candidate.y))
                .Take(5)
                .Select(candidate => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}@z{1}",
                    candidate.saveId,
                    candidate.hasZ
                        ? candidate.z.ToString(
                            "0",
                            CultureInfo.InvariantCulture)
                        : "?"))
                .ToArray();
            return overlaps.Length == 0
                ? "none"
                : string.Join(",", overlaps);
        }

        private static string FindWorldOverlapSummary(
            WorldTreasure source,
            IEnumerable<WorldTreasure> points)
        {
            string[] overlaps = points
                .Where(candidate =>
                    !object.ReferenceEquals(candidate, source)
                    && CandidateIsClose(
                        source.X,
                        source.Y,
                        candidate.X,
                        candidate.Y))
                .Take(5)
                .Select(candidate => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}@z{1}",
                    candidate.SaveId,
                    candidate.HasZ
                        ? candidate.Z.ToString(
                            "0",
                            CultureInfo.InvariantCulture)
                        : "?"))
                .ToArray();
            return overlaps.Length == 0
                ? "none"
                : string.Join(",", overlaps);
        }

        private static bool CandidateIsClose(
            double leftX,
            double leftY,
            double rightX,
            double rightY)
        {
            double deltaX = leftX - rightX;
            double deltaY = leftY - rightY;
            return deltaX * deltaX + deltaY * deltaY
                <= 250.0 * 250.0;
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
