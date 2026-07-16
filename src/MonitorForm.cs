using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TinyHwBar
{
    internal sealed class MonitorForm : Form
    {
        private const int BarWidth = 330;
        private const int BarHeight = 36;
        private const int EdgeMargin = 12;
        private const int TextHorizontalInset = 6;
        private const int SampleIntervalMilliseconds = 2000;
        private const int HistoryPersistenceIntervalMilliseconds = 30000;
        private static readonly DateTime UnixEpochUtc = new DateTime(
            1970,
            1,
            1,
            0,
            0,
            0,
            DateTimeKind.Utc);

        private readonly AppSettings loadedSettings;
        private readonly HardwareSampler sampler;
        private readonly Font primaryFont;
        private readonly Font compactFont;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem visibilityMenuItem;
        private readonly ToolStripMenuItem lockMenuItem;
        private readonly ToolStripMenuItem clickThroughMenuItem;
        private readonly ToolStripMenuItem opacityMenuItem;
        private readonly Icon applicationIcon;
        private readonly NotifyIcon notifyIcon;
        private readonly MetricHistory history;
        private readonly HistoryStore historyStore;
        private readonly object historyPersistenceSynchronization = new object();
        private readonly StartupManager startupManager;
        private readonly LoopbackApiOptions loopbackApiOptions;
        private readonly LoopbackApiServer loopbackApiServer;

        private System.Threading.Timer sampleTimer;
        private System.Threading.Timer historyPersistenceTimer;
        private DashboardForm dashboard;
        private HardwareSnapshot latestSnapshot;
        private int sampleBusy;
        private int historySaveBusy;
        private int closingStarted;
        private int stopping;
        private int updateCheckBusy;
        private int updatePackageBusy;
        private int telemetrySendBusy;
        private int historyPersistenceWarningScheduled;
        private int historyPersistenceWarningShown;
        private DateTime historyPersistenceStartUtc;
        private bool settingsPersistenceWarningShown;
        private bool locked;
        private bool clickThrough;
        private int opacityPercent;
        private bool dragging;
        private Point dragStartCursor;
        private Point dragStartLocation;
        private LoopbackApiSession loopbackApiSession;
        private string statusText = "CPU -- · RAM -- · GPU -- · VR -- · --°";

        internal MonitorForm()
        {
            loadedSettings = SettingsStore.Load();
            locked = loadedSettings.Locked;
            clickThrough = loadedSettings.ClickThrough;
            opacityPercent = loadedSettings.OpacityPercent;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(0x20, 0x24, 0x2A);
            ForeColor = Color.FromArgb(0xF0, 0xF3, 0xF6);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "TinyHwBar";
            AccessibleName = "TinyHwBar hardware monitor";
            TopMost = true;
            Opacity = opacityPercent / 100.0;
            ClientSize = new Size(BarWidth, BarHeight);
            applicationIcon = LoadApplicationIcon();
            Icon = applicationIcon;
            history = new MetricHistory();
            historyStore = new HistoryStore();
            historyPersistenceStartUtc = loadedSettings.PersistHistory
                ? DateTime.MinValue
                : DateTime.MaxValue;
            if (loadedSettings.PersistHistory)
            {
                HistoryPoint[] persistedPoints = historyStore.Load();
                foreach (HistoryPoint point in persistedPoints)
                {
                    history.Add(point);
                }
            }

            startupManager = new StartupManager(Application.ExecutablePath);
            RefreshStartupSettingFromRegistry();

            loopbackApiOptions = LoopbackApiOptions.CreateDefault();
            loopbackApiOptions.Enabled = loadedSettings.LoopbackApiEnabled;
            loopbackApiServer = new LoopbackApiServer(
                loopbackApiOptions,
                BuildLoopbackStatusJson);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint,
                true);

            primaryFont = new Font(
                "Segoe UI",
                13.0f,
                FontStyle.Regular,
                GraphicsUnit.Pixel);
            compactFont = new Font(
                "Segoe UI",
                12.0f,
                FontStyle.Regular,
                GraphicsUnit.Pixel);

            menu = new ContextMenuStrip();
            visibilityMenuItem = new ToolStripMenuItem();
            lockMenuItem = new ToolStripMenuItem("锁定位置");
            clickThroughMenuItem = new ToolStripMenuItem("鼠标穿透");
            opacityMenuItem = new ToolStripMenuItem("透明度");
            ToolStripMenuItem dashboardMenuItem = new ToolStripMenuItem("打开控制中心");
            ToolStripMenuItem resetMenuItem = new ToolStripMenuItem("重置到右上角");
            ToolStripMenuItem exitMenuItem = new ToolStripMenuItem("退出");

            foreach (int option in AppSettings.SupportedOpacityPercentages)
            {
                ToolStripMenuItem optionMenuItem = new ToolStripMenuItem(option + "%");
                optionMenuItem.Tag = option;
                optionMenuItem.Click += SetOpacity;
                opacityMenuItem.DropDownItems.Add(optionMenuItem);
            }

            visibilityMenuItem.Click += ToggleVisibility;
            dashboardMenuItem.Click += OpenDashboard;
            lockMenuItem.Click += ToggleLocked;
            clickThroughMenuItem.Click += ToggleClickThrough;
            resetMenuItem.Click += ResetPosition;
            exitMenuItem.Click += ExitApplication;

            menu.Items.Add(visibilityMenuItem);
            menu.Items.Add(dashboardMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(lockMenuItem);
            menu.Items.Add(clickThroughMenuItem);
            menu.Items.Add(opacityMenuItem);
            menu.Items.Add(resetMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitMenuItem);
            menu.Opening += UpdateMenuState;
            ContextMenuStrip = menu;

            notifyIcon = new NotifyIcon();
            notifyIcon.Text = "TinyHwBar";
            notifyIcon.Icon = applicationIcon;
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.DoubleClick += ToggleVisibility;
            notifyIcon.Visible = true;

            sampler = new HardwareSampler();
            sampler.SetGatewayLatencyEnabled(loadedSettings.GatewayLatencyEnabled);
            ApplyInitialPosition();
            SaveSettings();
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
                parameters.ExStyle |= NativeMethods.WsExToolWindow;
                parameters.ExStyle |= NativeMethods.WsExNoActivate;

                if (clickThrough)
                {
                    parameters.ExStyle |= (int)NativeMethods.WsExTransparent;
                }

                return parameters;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            SaveHistoryIfEnabled();
            sampleTimer = new System.Threading.Timer(
                SampleHardware,
                null,
                0,
                SampleIntervalMilliseconds);
            historyPersistenceTimer = new System.Threading.Timer(
                PersistHistory,
                null,
                HistoryPersistenceIntervalMilliseconds,
                HistoryPersistenceIntervalMilliseconds);

            ApplyClickThroughStyle();
            ApplyPersistedAdvancedFeatures();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            TextFormatFlags measureFlags =
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine;

            Size measured = TextRenderer.MeasureText(
                statusText,
                primaryFont,
                Size.Empty,
                measureFlags);

            Font font = measured.Width > BarWidth - (TextHorizontalInset * 2)
                ? compactFont
                : primaryFont;

            Rectangle textBounds = new Rectangle(
                TextHorizontalInset,
                0,
                BarWidth - (TextHorizontalInset * 2),
                BarHeight);

            TextRenderer.DrawText(
                e.Graphics,
                statusText,
                font,
                textBounds,
                ForeColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Left && !locked && !clickThrough)
            {
                dragging = true;
                dragStartCursor = Cursor.Position;
                dragStartLocation = Location;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!dragging)
            {
                return;
            }

            Point cursor = Cursor.Position;
            Location = CalculateDragLocation(
                dragStartCursor,
                dragStartLocation,
                cursor);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Left && dragging)
            {
                dragging = false;
                Capture = false;
                CorrectPositionForCurrentScreens();
                SaveSettings(true);
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);

            if (!Capture && dragging)
            {
                dragging = false;
                if (Interlocked.CompareExchange(ref stopping, 0, 0) == 0 && !IsDisposed)
                {
                    CorrectPositionForCurrentScreens();
                    SaveSettings(true);
                }
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WmDpiChanged)
            {
                NativeMethods.Rect suggestedBounds = (NativeMethods.Rect)Marshal.PtrToStructure(
                    message.LParam,
                    typeof(NativeMethods.Rect));

                base.WndProc(ref message);

                SetBounds(
                    suggestedBounds.Left,
                    suggestedBounds.Top,
                    BarWidth,
                    BarHeight,
                    BoundsSpecified.All);
                if (dragging)
                {
                    RebaseDragAnchorIfNeeded();
                }
                else
                {
                    CorrectPositionForCurrentScreens();
                    SaveSettings();
                }
                return;
            }

            if (message.Msg == NativeMethods.WmDisplayChange)
            {
                base.WndProc(ref message);
                if (dragging)
                {
                    RebaseDragAnchorIfNeeded();
                }
                else
                {
                    CorrectPositionForCurrentScreens();
                    SaveSettings();
                }
                return;
            }

            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Interlocked.CompareExchange(ref closingStarted, 1, 0) != 0)
            {
                e.Cancel = true;
                return;
            }

            Interlocked.Exchange(ref stopping, 1);
            notifyIcon.Visible = false;
            menu.Enabled = false;

            if (sampleTimer != null)
            {
                sampleTimer.Dispose();
                sampleTimer = null;
            }

            if (historyPersistenceTimer != null)
            {
                historyPersistenceTimer.Dispose();
                historyPersistenceTimer = null;
            }

            sampler.Dispose();
            loopbackApiServer.Dispose();
            loopbackApiSession = null;
            HistorySaveFailure finalHistoryFailure = SaveHistoryCore(true);
            if (finalHistoryFailure != null &&
                e.CloseReason == CloseReason.UserClosing)
            {
                ShowHistoryPersistenceWarning(
                    finalHistoryFailure.ToSafeDisplayText(),
                    true);
            }

            history.Clear();

            if (dashboard != null && !dashboard.IsDisposed)
            {
                dashboard.SettingsApplied -= ApplyDashboardSettings;
                dashboard.HistoryCleared -= ClearPersistedHistory;
                dashboard.UpdateCheckRequested -= CheckForUpdate;
                dashboard.TelemetryPreviewRequested -= PreviewTelemetry;
                dashboard.DiagnosticPreviewRequested -= PreviewDiagnostic;
                dashboard.Close();
                dashboard = null;
            }

            SaveSettings();
            notifyIcon.Visible = false;
            notifyIcon.Icon = null;
            notifyIcon.Dispose();
            menu.Dispose();
            Icon = null;
            applicationIcon.Dispose();
            primaryFont.Dispose();
            compactFont.Dispose();

            base.OnFormClosing(e);
        }

        private void SampleHardware(object state)
        {
            if (Interlocked.CompareExchange(ref stopping, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref sampleBusy, 1, 0) != 0)
            {
                return;
            }

            try
            {
                HardwareSnapshot snapshot = sampler.Sample();
                lock (historyPersistenceSynchronization)
                {
                    if (Interlocked.CompareExchange(ref stopping, 0, 0) != 0)
                    {
                        return;
                    }

                    history.Add(
                        snapshot,
                        snapshot.NetworkReceiveBytesPerSecond,
                        snapshot.NetworkSendBytesPerSecond,
                        snapshot.IntegratedGpuPercent);
                }
                string newStatusText = snapshot.ToDisplayText();

                if (Interlocked.CompareExchange(ref stopping, 0, 0) != 0 ||
                    !IsHandleCreated ||
                    IsDisposed)
                {
                    return;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (Interlocked.CompareExchange(ref stopping, 0, 0) == 0)
                        {
                            Interlocked.Exchange(ref latestSnapshot, snapshot);
                            statusText = newStatusText;
                            Invalidate();

                            if (dashboard != null && !dashboard.IsDisposed)
                            {
                                dashboard.UpdateSnapshot(snapshot);
                            }
                        }
                    });
                }
                catch (ObjectDisposedException)
                {
                    // The form is closing.
                }
                catch (InvalidOperationException)
                {
                    // The form is closing.
                }
            }
            catch (Exception)
            {
                // Individual sampler failures are represented by placeholders.
            }
            finally
            {
                Interlocked.Exchange(ref sampleBusy, 0);
            }
        }

        private void PersistHistory(object state)
        {
            if (Interlocked.CompareExchange(ref historySaveBusy, 1, 0) != 0)
            {
                return;
            }

            try
            {
                SaveHistoryIfEnabled();
            }
            finally
            {
                Interlocked.Exchange(ref historySaveBusy, 0);
            }
        }

        private void ApplyInitialPosition()
        {
            Rectangle savedBounds = new Rectangle(
                loadedSettings.Left,
                loadedSettings.Top,
                BarWidth,
                BarHeight);

            if (loadedSettings.HasSavedPosition && IsFullyInsideAnyScreenBounds(savedBounds))
            {
                SetBounds(
                    loadedSettings.Left,
                    loadedSettings.Top,
                    BarWidth,
                    BarHeight,
                    BoundsSpecified.All);
            }
            else
            {
                SetDefaultPosition();
            }
        }

        private static bool IsFullyInsideAnyScreenBounds(Rectangle bounds)
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                if (IsFullyInsideBounds(bounds, screen.Bounds))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsFullyInsideBounds(
            Rectangle windowBounds,
            Rectangle screenBounds)
        {
            if (windowBounds.Width <= 0 ||
                windowBounds.Height <= 0 ||
                screenBounds.Width <= 0 ||
                screenBounds.Height <= 0)
            {
                return false;
            }

            long windowRight = (long)windowBounds.X + windowBounds.Width;
            long windowBottom = (long)windowBounds.Y + windowBounds.Height;
            long screenRight = (long)screenBounds.X + screenBounds.Width;
            long screenBottom = (long)screenBounds.Y + screenBounds.Height;

            return windowBounds.X >= screenBounds.X &&
                windowBounds.Y >= screenBounds.Y &&
                windowRight <= screenRight &&
                windowBottom <= screenBottom;
        }

        internal static Point CalculateDragLocation(
            Point startCursor,
            Point startLocation,
            Point currentCursor)
        {
            return new Point(
                startLocation.X + currentCursor.X - startCursor.X,
                startLocation.Y + currentCursor.Y - startCursor.Y);
        }

        private void SetDefaultPosition()
        {
            Screen primaryScreen = Screen.PrimaryScreen;
            Rectangle workingArea = primaryScreen != null
                ? primaryScreen.WorkingArea
                : Screen.FromPoint(Point.Empty).WorkingArea;

            int left = Math.Max(
                workingArea.Left,
                workingArea.Right - BarWidth - EdgeMargin);
            int top = Math.Max(
                workingArea.Top,
                workingArea.Top + EdgeMargin);

            SetBounds(left, top, BarWidth, BarHeight, BoundsSpecified.All);
        }

        private void CorrectPositionForCurrentScreens()
        {
            bool intersectsAnyScreen = false;

            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.Bounds.IntersectsWith(Bounds))
                {
                    intersectsAnyScreen = true;
                    break;
                }
            }

            if (!intersectsAnyScreen)
            {
                SetDefaultPosition();
                RebaseDragAnchorIfNeeded();
                return;
            }

            Rectangle screenBounds = Screen.FromRectangle(Bounds).Bounds;
            Rectangle correctedBounds = ConstrainToBounds(Bounds, screenBounds);

            SetBounds(
                correctedBounds.Left,
                correctedBounds.Top,
                correctedBounds.Width,
                correctedBounds.Height,
                BoundsSpecified.All);
            RebaseDragAnchorIfNeeded();
        }

        private void RebaseDragAnchorIfNeeded()
        {
            if (!dragging)
            {
                return;
            }

            dragStartCursor = Cursor.Position;
            dragStartLocation = Location;
        }

        internal static Rectangle ConstrainToBounds(
            Rectangle windowBounds,
            Rectangle screenBounds)
        {
            int maximumLeft = Math.Max(
                screenBounds.Left,
                screenBounds.Right - windowBounds.Width);
            int maximumTop = Math.Max(
                screenBounds.Top,
                screenBounds.Bottom - windowBounds.Height);
            int correctedLeft = Math.Min(
                Math.Max(windowBounds.Left, screenBounds.Left),
                maximumLeft);
            int correctedTop = Math.Min(
                Math.Max(windowBounds.Top, screenBounds.Top),
                maximumTop);

            return new Rectangle(
                correctedLeft,
                correctedTop,
                windowBounds.Width,
                windowBounds.Height);
        }

        private void UpdateMenuState(object sender, CancelEventArgs e)
        {
            visibilityMenuItem.Text = Visible ? "隐藏监控条" : "显示监控条";
            lockMenuItem.Checked = locked;
            clickThroughMenuItem.Checked = clickThrough;

            foreach (ToolStripItem item in opacityMenuItem.DropDownItems)
            {
                ToolStripMenuItem optionMenuItem = item as ToolStripMenuItem;
                if (optionMenuItem != null)
                {
                    optionMenuItem.Checked = (int)optionMenuItem.Tag == opacityPercent;
                }
            }
        }

        private void ToggleVisibility(object sender, EventArgs e)
        {
            if (Visible)
            {
                Hide();
            }
            else
            {
                Show();
                TopMost = true;
            }
        }

        private void OpenDashboard(object sender, EventArgs e)
        {
            if (dashboard == null || dashboard.IsDisposed)
            {
                dashboard = new DashboardForm(loadedSettings.Clone(), history);
                dashboard.SettingsApplied += ApplyDashboardSettings;
                dashboard.HistoryCleared += ClearPersistedHistory;
                dashboard.UpdateCheckRequested += CheckForUpdate;
                dashboard.TelemetryPreviewRequested += PreviewTelemetry;
                dashboard.DiagnosticPreviewRequested += PreviewDiagnostic;
                dashboard.FormClosed += DashboardClosed;
                dashboard.Show();
            }
            else
            {
                if (dashboard.WindowState == FormWindowState.Minimized)
                {
                    dashboard.WindowState = FormWindowState.Normal;
                }

                dashboard.Show();
                dashboard.Activate();
            }

            dashboard.RefreshSettings(loadedSettings.Clone());
            RefreshAdvancedStatus();
            HardwareSnapshot snapshot = ReadLatestSnapshot();
            if (snapshot != null)
            {
                dashboard.UpdateSnapshot(snapshot);
            }
        }

        private void DashboardClosed(object sender, FormClosedEventArgs e)
        {
            DashboardForm closedDashboard = sender as DashboardForm;
            if (closedDashboard != null)
            {
                closedDashboard.SettingsApplied -= ApplyDashboardSettings;
                closedDashboard.HistoryCleared -= ClearPersistedHistory;
                closedDashboard.UpdateCheckRequested -= CheckForUpdate;
                closedDashboard.TelemetryPreviewRequested -= PreviewTelemetry;
                closedDashboard.DiagnosticPreviewRequested -= PreviewDiagnostic;
                closedDashboard.FormClosed -= DashboardClosed;
            }

            if (ReferenceEquals(dashboard, closedDashboard))
            {
                dashboard = null;
            }
        }

        private void ApplyDashboardSettings(
            object sender,
            DashboardSettingsChangedEventArgs e)
        {
            string normalizedUpdateEndpoint;
            string normalizedTelemetryEndpoint;
            if (!TryNormalizeOptionalEndpoint(
                    e.UpdateManifestUrl,
                    "更新清单",
                    e.AutomaticUpdateEnabled,
                    out normalizedUpdateEndpoint) ||
                !TryNormalizeOptionalEndpoint(
                    e.TelemetryEndpoint,
                    "遥测",
                    e.TelemetryEnabled,
                    out normalizedTelemetryEndpoint))
            {
                if (dashboard != null && !dashboard.IsDisposed)
                {
                    dashboard.RefreshSettings(loadedSettings.Clone());
                }

                return;
            }

            locked = e.Locked;
            clickThrough = e.ClickThrough;
            opacityPercent = e.OpacityPercent;
            bool historyPersistenceChanged = loadedSettings.PersistHistory != e.PersistHistory;
            lock (historyPersistenceSynchronization)
            {
                loadedSettings.PersistHistory = e.PersistHistory;
                if (historyPersistenceChanged)
                {
                    historyPersistenceStartUtc = e.PersistHistory
                        ? DateTime.UtcNow
                        : DateTime.MaxValue;
                }
            }
            loadedSettings.GatewayLatencyEnabled = e.GatewayLatencyEnabled;
            sampler.SetGatewayLatencyEnabled(e.GatewayLatencyEnabled);
            Opacity = opacityPercent / 100.0;
            ApplyClickThroughStyle();

            if (historyPersistenceChanged)
            {
                if (loadedSettings.PersistHistory)
                {
                    SaveHistoryIfEnabled();
                }
                else
                {
                    ClearPersistedHistoryWithWarning();
                }
            }

            bool startupEnabled = ApplyStartupPreference(e.StartupEnabled);
            bool automaticUpdateEnabled = e.AutomaticUpdateEnabled;
            bool automaticUpdateTargetChanged = !string.Equals(
                normalizedUpdateEndpoint,
                loadedSettings.UpdateManifestUrl,
                StringComparison.Ordinal);
            if (automaticUpdateEnabled &&
                (!loadedSettings.AutomaticUpdateEnabled || automaticUpdateTargetChanged))
            {
                DialogResult confirmation = MessageBox.Show(
                    this,
                    "日常使用影响：以后每次启动 TinyHwBar 都会向下面的 HTTPS 地址发送一次更新清单 GET 请求：\r\n\r\n" +
                    normalizedUpdateEndpoint +
                    "\r\n\r\n它只检查版本，不会自动下载、替换或安装。取消勾选并应用即可恢复。是否启用？",
                    "启用自动更新检查",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirmation != DialogResult.Yes)
                {
                    automaticUpdateEnabled = loadedSettings.AutomaticUpdateEnabled;
                    normalizedUpdateEndpoint = loadedSettings.UpdateManifestUrl;
                }
            }

            bool loopbackApiEnabled = ApplyLoopbackApiPreference(e.LoopbackApiEnabled);
            bool telemetryEnabled = e.TelemetryEnabled;
            if (telemetryEnabled && !loadedSettings.TelemetryEnabled)
            {
                DialogResult confirmation = MessageBox.Show(
                    this,
                    "遥测仍不会自动发送。每次发送前都会展示目标地址、字段、字节数和完整 JSON，并再次询问。\r\n\r\n取消勾选并应用即可恢复。是否允许进入可发送状态？",
                    "启用逐次确认的遥测",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirmation != DialogResult.Yes)
                {
                    telemetryEnabled = false;
                }
            }

            loadedSettings.StartupEnabled = startupEnabled;
            loadedSettings.AutomaticUpdateEnabled = automaticUpdateEnabled;
            loadedSettings.UpdateManifestUrl = normalizedUpdateEndpoint;
            loadedSettings.LoopbackApiEnabled = loopbackApiEnabled;
            loadedSettings.TelemetryEnabled = telemetryEnabled;
            loadedSettings.TelemetryEndpoint = normalizedTelemetryEndpoint;

            SaveSettings(true);
            RefreshAdvancedStatus();
        }

        private void ClearPersistedHistory(object sender, EventArgs e)
        {
            lock (historyPersistenceSynchronization)
            {
                history.Clear();
            }

            ClearPersistedHistoryWithWarning();
        }

        private void ClearPersistedHistoryWithWarning()
        {
            bool cleared;
            lock (historyPersistenceSynchronization)
            {
                cleared = historyStore.Clear();
            }

            if (!cleared)
            {
                MessageBox.Show(
                    this,
                    "内存历史已清除，但本地历史文件无法删除。请退出 TinyHwBar 后手动删除 %LOCALAPPDATA%\\TinyHwBar\\history.csv。",
                    "TinyHwBar",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void RefreshStartupSettingFromRegistry()
        {
            try
            {
                StartupRegistrationState state = startupManager.GetStatus();
                loadedSettings.StartupEnabled =
                    state == StartupRegistrationState.EnabledForCurrentExecutable;

                if (dashboard != null && !dashboard.IsDisposed)
                {
                    string status;
                    switch (state)
                    {
                        case StartupRegistrationState.EnabledForCurrentExecutable:
                            status = "当前用户启动项已启用，指向本程序。";
                            break;
                        case StartupRegistrationState.DifferentCommand:
                            status = "存在同名但不同的启动命令；TinyHwBar 未改动它。";
                            break;
                        default:
                            status = "当前用户启动项已关闭。";
                            break;
                    }

                    dashboard.SetStartupState(loadedSettings.StartupEnabled, status);
                }
            }
            catch (Exception)
            {
                loadedSettings.StartupEnabled = false;
                if (dashboard != null && !dashboard.IsDisposed)
                {
                    dashboard.SetStartupState(
                        false,
                        "无法读取当前用户启动项；未进行任何修改。");
                }
            }
        }

        private void ApplyPersistedAdvancedFeatures()
        {
            bool settingsChanged = false;

            if (loadedSettings.LoopbackApiEnabled)
            {
                loopbackApiOptions.Enabled = true;
                try
                {
                    loopbackApiSession = loopbackApiServer.Start();
                }
                catch (Exception)
                {
                    loopbackApiOptions.Enabled = false;
                    loadedSettings.LoopbackApiEnabled = false;
                    loopbackApiSession = null;
                    settingsChanged = true;
                }
            }

            if (loadedSettings.AutomaticUpdateEnabled &&
                !string.IsNullOrWhiteSpace(loadedSettings.UpdateManifestUrl))
            {
                QueueUpdateCheck(
                    UpdateCheckTrigger.ExplicitAutomatic,
                    loadedSettings.UpdateManifestUrl,
                    true);
            }

            RefreshAdvancedStatus();
            if (settingsChanged)
            {
                SaveSettings();
            }
        }

        private bool ApplyStartupPreference(bool requestedEnabled)
        {
            StartupRegistrationState currentState;
            try
            {
                currentState = startupManager.GetStatus();
            }
            catch (Exception exception)
            {
                ShowAdvancedOperationError("读取启动项失败", exception);
                return loadedSettings.StartupEnabled;
            }

            bool currentlyEnabled =
                currentState == StartupRegistrationState.EnabledForCurrentExecutable;
            if (requestedEnabled == currentlyEnabled)
            {
                return currentlyEnabled;
            }

            try
            {
                if (requestedEnabled)
                {
                    DialogResult confirmation = MessageBox.Show(
                        this,
                        "日常使用影响：这会在当前用户的 Windows 登录启动项中写入 TinyHwBar，之后登录 Windows 会自动运行当前这个 EXE。\r\n\r\n取消勾选并应用即可删除本程序匹配的启动项。是否继续？",
                        "启用开机启动",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (confirmation != DialogResult.Yes)
                    {
                        return currentlyEnabled;
                    }

                    if (currentState == StartupRegistrationState.DifferentCommand)
                    {
                        DialogResult replaceConfirmation = MessageBox.Show(
                            this,
                            "检测到同名但指向其他命令的启动项。只有选择“是”才会把它替换为当前 TinyHwBar；选择“否”将保持原样。是否替换？",
                            "确认替换同名启动项",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2);
                        if (replaceConfirmation != DialogResult.Yes)
                        {
                            return false;
                        }

                        startupManager.ReplaceForCurrentUserAfterConfirmation(true);
                    }
                    else
                    {
                        startupManager.EnableForCurrentUser();
                    }

                    return true;
                }

                DialogResult disableConfirmation = MessageBox.Show(
                    this,
                    "日常使用影响：这会删除仅与当前 TinyHwBar EXE 精确匹配的当前用户登录启动项。下次登录 Windows 将不再自动启动它；重新勾选并应用即可恢复。是否继续？",
                    "关闭开机启动",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (disableConfirmation != DialogResult.Yes)
                {
                    return true;
                }

                if (!startupManager.DisableForCurrentUser())
                {
                    MessageBox.Show(
                        this,
                        "启动项已被其他命令占用，因此没有删除任何注册表值。",
                        "TinyHwBar",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return currentlyEnabled;
                }

                return false;
            }
            catch (Exception exception)
            {
                ShowAdvancedOperationError("修改启动项失败", exception);
                return currentlyEnabled;
            }
        }

        private bool ApplyLoopbackApiPreference(bool requestedEnabled)
        {
            if (!requestedEnabled)
            {
                loopbackApiOptions.Enabled = false;
                loopbackApiServer.Stop();
                loopbackApiSession = null;
                return false;
            }

            if (loopbackApiServer.IsRunning && loopbackApiSession != null)
            {
                return true;
            }

            DialogResult confirmation = MessageBox.Show(
                this,
                "日常使用影响：TinyHwBar 会在 127.0.0.1 的随机高端口启动只限本机的只读状态接口。接口只返回数值型硬件指标，并要求本次运行随机生成的 Bearer 令牌；不会监听局域网地址。\r\n\r\n退出程序只停止本次监听；启用状态会保存并在下次启动时恢复。若要持续关闭，请取消勾选并应用。是否启用？",
                "启用本机状态 API",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return false;
            }

            loopbackApiOptions.Enabled = true;
            try
            {
                loopbackApiSession = loopbackApiServer.Start();
                return true;
            }
            catch (Exception exception)
            {
                loopbackApiOptions.Enabled = false;
                loopbackApiSession = null;
                ShowAdvancedOperationError("启动本机 API 失败", exception);
                return false;
            }
        }

        private bool TryNormalizeOptionalEndpoint(
            string endpointText,
            string displayName,
            bool required,
            out string normalizedEndpoint)
        {
            normalizedEndpoint = string.Empty;
            if (string.IsNullOrWhiteSpace(endpointText))
            {
                if (required)
                {
                    MessageBox.Show(
                        this,
                        displayName + "功能启用前必须填写 HTTPS 地址。",
                        "TinyHwBar",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return false;
                }

                return true;
            }

            Uri endpoint;
            string endpointError;
            if (!PublicHttpsEndpointPolicy.TryCreate(
                    endpointText,
                    out endpoint,
                    out endpointError))
            {
                MessageBox.Show(
                    this,
                    displayName + "地址被拒绝。只接受不含 query、凭据或片段，且地址预检不指向本地或私网的 HTTPS 地址。",
                    "TinyHwBar",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            normalizedEndpoint = endpoint.AbsoluteUri;
            return true;
        }

        private void CheckForUpdate(
            object sender,
            DashboardEndpointActionEventArgs e)
        {
            string normalizedEndpoint;
            if (!TryNormalizeOptionalEndpoint(
                    e.Endpoint,
                    "更新清单",
                    true,
                    out normalizedEndpoint))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref updateCheckBusy, 0, 0) != 0)
            {
                SetUpdateStatus("已有更新检查正在进行，请稍候。");
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                this,
                "将向下面的地址发送一次 HTTPS GET 请求：\r\n\r\n" +
                normalizedEndpoint +
                "\r\n\r\n只读取最多 64 KiB 的版本清单；本次不会下载、安装或替换程序。是否继续？",
                "手动检查更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                SetUpdateStatus("已取消，未发送网络请求。");
                return;
            }

            QueueUpdateCheck(UpdateCheckTrigger.Manual, normalizedEndpoint, true);
        }

        private void QueueUpdateCheck(
            UpdateCheckTrigger trigger,
            string endpoint,
            bool automaticChecksEnabled)
        {
            if (Interlocked.CompareExchange(ref updateCheckBusy, 1, 0) != 0)
            {
                SetUpdateStatus("已有更新检查正在进行，请稍候。");
                return;
            }

            SetUpdateStatus("正在检查更新…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateCheckResult result = null;
                string resultText;
                try
                {
                    UpdateServiceOptions options = UpdateServiceOptions.CreateDefault();
                    options.AutomaticChecksEnabled = automaticChecksEnabled;
                    options.ManifestEndpoint = endpoint ?? string.Empty;
                    UpdateService service = new UpdateService(
                        Assembly.GetExecutingAssembly().GetName().Version,
                        options,
                        new HttpWebRequestUpdateTransport());
                    result = service.CheckNow(trigger);
                    resultText = FormatUpdateCheckResult(result);
                }
                catch (Exception)
                {
                    resultText = "更新检查失败；未下载或安装任何内容。";
                }
                finally
                {
                    Interlocked.Exchange(ref updateCheckBusy, 0);
                }

                PostUpdateCheckResult(result, resultText, trigger);
            });
        }

        private void PostUpdateCheckResult(
            UpdateCheckResult result,
            string resultText,
            UpdateCheckTrigger trigger)
        {
            if (Interlocked.CompareExchange(ref stopping, 0, 0) != 0 ||
                IsDisposed ||
                !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (Interlocked.CompareExchange(ref stopping, 0, 0) == 0)
                        {
                            PostUpdateCheckResult(result, resultText, trigger);
                        }
                    });
                }
                catch (ObjectDisposedException)
                {
                    // The form is closing.
                }
                catch (InvalidOperationException)
                {
                    // The form is closing.
                }

                return;
            }

            SetUpdateStatus(resultText);
            if (result == null ||
                result.Status != UpdateCheckStatus.Succeeded ||
                !result.UpdateAvailable ||
                result.Manifest == null)
            {
                return;
            }

            if (trigger != UpdateCheckTrigger.Manual)
            {
                notifyIcon.ShowBalloonTip(
                    8000,
                    "TinyHwBar 有可用更新",
                    "版本 " + result.Manifest.Version +
                    " 可用。打开控制中心并手动检查后，可选择下载到本地暂存区。",
                    ToolTipIcon.Info);
                return;
            }

            OfferUpdatePackageStaging(result.Manifest);
        }

        private void OfferUpdatePackageStaging(UpdateManifest manifest)
        {
            if (manifest == null || manifest.DownloadUri == null)
            {
                return;
            }

            string stagingDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TinyHwBar",
                "updates");
            DialogResult confirmation = MessageBox.Show(
                this,
                "发现版本 " + manifest.Version + "。\r\n\r\n" +
                "若继续，将从下面的 HTTPS 地址下载最多 32 MiB：\r\n" +
                manifest.DownloadUri.AbsoluteUri +
                "\r\n\r\n下载后会校验清单中的 SHA-256，并只暂存为：\r\n" +
                Path.Combine(stagingDirectory, UpdatePackageService.StagedFileName) +
                "\r\n\r\n它不会自动运行、替换当前 EXE 或安装。是否下载并暂存？",
                "下载并校验更新包",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                SetUpdateStatus("发现新版本，但已取消下载；没有写入更新包。");
                return;
            }

            QueueUpdatePackageStage(manifest, stagingDirectory);
        }

        private void QueueUpdatePackageStage(
            UpdateManifest manifest,
            string stagingDirectory)
        {
            if (Interlocked.CompareExchange(ref updatePackageBusy, 1, 0) != 0)
            {
                SetUpdateStatus("已有更新包下载正在进行，请稍候。");
                return;
            }

            SetUpdateStatus("正在下载并校验已确认的更新包…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdatePackageStageResult result;
                try
                {
                    UpdatePackageService service = new UpdatePackageService(
                        new HttpWebRequestUpdatePackageTransport());
                    result = service.StageAfterConfirmation(
                        manifest,
                        stagingDirectory,
                        true);
                }
                catch (Exception)
                {
                    result = UpdatePackageStageResult.Create(
                        UpdatePackageStageStatus.TransportFailed);
                }
                finally
                {
                    Interlocked.Exchange(ref updatePackageBusy, 0);
                }

                SetUpdateStatus(FormatUpdatePackageStageResult(result));
            });
        }

        private static string FormatUpdatePackageStageResult(
            UpdatePackageStageResult result)
        {
            if (result == null)
            {
                return "更新包下载或暂存失败；当前程序未被修改。";
            }

            switch (result.Status)
            {
                case UpdatePackageStageStatus.Succeeded:
                    return "更新包已通过 SHA-256 校验并暂存到 " +
                        result.StagedPath +
                        "；未自动运行、替换或安装。";
                case UpdatePackageStageStatus.NotConfirmed:
                    return "未确认下载；没有写入更新包。";
                case UpdatePackageStageStatus.PackageTooLarge:
                    return "更新包超过 32 MiB 上限，已拒绝。";
                case UpdatePackageStageStatus.HashMismatch:
                    return "更新包 SHA-256 不匹配，已拒绝且未替换暂存文件。";
                case UpdatePackageStageStatus.EndpointRejected:
                    return "更新包地址未通过 HTTPS 地址预检。";
                default:
                    return "更新包下载或暂存失败；当前程序未被修改。";
            }
        }

        private static string FormatUpdateCheckResult(UpdateCheckResult result)
        {
            if (result == null)
            {
                return "更新检查失败；未下载或安装任何内容。";
            }

            switch (result.Status)
            {
                case UpdateCheckStatus.Succeeded:
                    if (result.Manifest == null)
                    {
                        return "更新清单无效；未下载或安装任何内容。";
                    }

                    return result.UpdateAvailable
                        ? "发现版本 " + result.Manifest.Version + "。当前仅完成清单检查，尚未下载或安装。"
                        : "当前已是最新版本；没有下载或安装任何内容。";
                case UpdateCheckStatus.AutomaticChecksDisabled:
                    return "自动更新检查已关闭。";
                case UpdateCheckStatus.EndpointNotConfigured:
                    return "未配置更新清单地址。";
                case UpdateCheckStatus.EndpointRejected:
                    return "更新清单地址未通过 HTTPS 地址预检。";
                case UpdateCheckStatus.InvalidManifest:
                    return "服务器返回的更新清单无效。";
                default:
                    return "更新检查失败；未下载或安装任何内容。";
            }
        }

        private void PreviewTelemetry(
            object sender,
            DashboardEndpointActionEventArgs e)
        {
            HardwareSnapshot snapshot = ReadLatestSnapshot();
            TelemetryDraft draft = new TelemetryDraft(
                "manual_preview",
                GetApplicationVersionToken(),
                snapshot == null ? "metrics_unavailable" : "metrics_available");
            PreviewAndMaybeSendTelemetry(e, draft, null, "遥测事件");
        }

        private void PreviewDiagnostic(
            object sender,
            DashboardEndpointActionEventArgs e)
        {
            List<DiagnosticLogEntry> entries = new List<DiagnosticLogEntry>();
            HardwareSnapshot snapshot = ReadLatestSnapshot();
            if (snapshot == null)
            {
                entries.Add(
                    new DiagnosticLogEntry(
                        DiagnosticLogSeverity.Warning,
                        "metrics_unavailable",
                        1));
            }
            else
            {
                entries.Add(
                    new DiagnosticLogEntry(
                        GetDiagnosticSeverity(
                            snapshot.IntegratedAdapterStatus == IntelGpuDataStatus.Available),
                        "integrated_" +
                        snapshot.IntegratedAdapterStatus.ToString().ToLowerInvariant(),
                        1));
                entries.Add(
                    new DiagnosticLogEntry(
                        GetDiagnosticSeverity(
                            snapshot.GatewayLatencyStatus == GatewayLatencyStatus.Available ||
                            snapshot.GatewayLatencyStatus == GatewayLatencyStatus.Disabled),
                        "gateway_" + snapshot.GatewayLatencyStatus.ToString().ToLowerInvariant(),
                        1));
                entries.Add(
                    new DiagnosticLogEntry(
                        GetDiagnosticSeverity(snapshot.GpuMode != GpuDisplayMode.Unavailable),
                        "discrete_" + snapshot.GpuMode.ToString().ToLowerInvariant(),
                        1));
            }

            DiagnosticLogDraft draft = new DiagnosticLogDraft(
                GetApplicationVersionToken(),
                entries.ToArray());
            PreviewAndMaybeSendTelemetry(e, null, draft, "诊断摘要");
        }

        private void PreviewAndMaybeSendTelemetry(
            DashboardEndpointActionEventArgs e,
            TelemetryDraft telemetryDraft,
            DiagnosticLogDraft diagnosticDraft,
            string payloadLabel)
        {
            string normalizedEndpoint;
            if (!TryNormalizeOptionalEndpoint(
                    e.Endpoint,
                    "遥测",
                    true,
                    out normalizedEndpoint))
            {
                return;
            }

            bool configurationApplied = loadedSettings.TelemetryEnabled &&
                e.Enabled &&
                string.Equals(
                    loadedSettings.TelemetryEndpoint,
                    normalizedEndpoint,
                    StringComparison.Ordinal);
            TelemetryOptions options = TelemetryOptions.CreateDefault();
            options.Enabled = configurationApplied;
            options.Endpoint = normalizedEndpoint;
            TelemetryService service = new TelemetryService(
                options,
                new HttpWebRequestTelemetryTransport());
            TelemetryPreview preview;
            try
            {
                preview = telemetryDraft != null
                    ? service.BuildPreview(telemetryDraft)
                    : service.BuildDiagnosticLogPreview(diagnosticDraft);
            }
            catch (Exception exception)
            {
                ShowAdvancedOperationError("生成发送预览失败", exception);
                return;
            }

            string previewText =
                "目标：" + preview.DestinationEndpoint +
                "\r\n字段：" + string.Join(", ", preview.GetFieldNames()) +
                "\r\nUTF-8 字节数：" + preview.Utf8ByteCount.ToString(CultureInfo.InvariantCulture) +
                "\r\n\r\n完整 JSON：\r\n" + preview.PayloadJson;

            if (!configurationApplied)
            {
                MessageBox.Show(
                    this,
                    previewText + "\r\n\r\n当前设置尚未应用，因此这里只预览，不会发送。",
                    payloadLabel + "预览",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                SetTelemetryStatus("已生成本地预览；未发送任何数据。");
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                this,
                previewText + "\r\n\r\n选择“是”会立即向上述目标发送一次 HTTPS POST；选择“否”则不发送。",
                "逐次确认发送 " + payloadLabel,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                SetTelemetryStatus("已取消；未发送任何数据。");
                return;
            }

            QueueTelemetrySend(service, preview, payloadLabel);
        }

        private void QueueTelemetrySend(
            TelemetryService service,
            TelemetryPreview preview,
            string payloadLabel)
        {
            if (Interlocked.CompareExchange(ref telemetrySendBusy, 1, 0) != 0)
            {
                SetTelemetryStatus("已有发送正在进行，请稍候。");
                return;
            }

            SetTelemetryStatus("正在发送已确认的" + payloadLabel + "…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                TelemetrySendResult result;
                try
                {
                    result = service.ConfirmAndSend(preview, true);
                }
                catch (Exception)
                {
                    result = new TelemetrySendResult(TelemetrySendStatus.TransportFailed);
                }
                finally
                {
                    Interlocked.Exchange(ref telemetrySendBusy, 0);
                }

                SetTelemetryStatus(FormatTelemetrySendResult(result, payloadLabel));
            });
        }

        private static string FormatTelemetrySendResult(
            TelemetrySendResult result,
            string payloadLabel)
        {
            if (result == null)
            {
                return payloadLabel + "发送失败。";
            }

            switch (result.Status)
            {
                case TelemetrySendStatus.Succeeded:
                    return payloadLabel + "已按本次确认发送。";
                case TelemetrySendStatus.UserConfirmationRequired:
                    return "未取得本次发送确认；没有发送。";
                case TelemetrySendStatus.TelemetryDisabled:
                    return "遥测设置已关闭；没有发送。";
                case TelemetrySendStatus.EndpointNotConfigured:
                case TelemetrySendStatus.EndpointRejected:
                    return "遥测目标无效；没有发送。";
                case TelemetrySendStatus.PreviewInvalid:
                    return "发送预览已失效；请重新预览并确认。";
                default:
                    return payloadLabel + "发送失败。";
            }
        }

        private static DiagnosticLogSeverity GetDiagnosticSeverity(bool available)
        {
            return available
                ? DiagnosticLogSeverity.Information
                : DiagnosticLogSeverity.Warning;
        }

        private static string GetApplicationVersionToken()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "2.0.0.0" : version.ToString();
        }

        private void SetUpdateStatus(string text)
        {
            PostStatusToDashboard(true, text);
        }

        private void SetTelemetryStatus(string text)
        {
            PostStatusToDashboard(false, text);
        }

        private void PostStatusToDashboard(bool isUpdateStatus, string text)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        PostStatusToDashboard(isUpdateStatus, text);
                    });
                }
                catch (ObjectDisposedException)
                {
                    // The form is closing.
                }
                catch (InvalidOperationException)
                {
                    // The form is closing.
                }

                return;
            }

            if (dashboard == null || dashboard.IsDisposed)
            {
                return;
            }

            if (isUpdateStatus)
            {
                dashboard.SetUpdateStatus(text);
            }
            else
            {
                dashboard.SetTelemetryStatus(text);
            }
        }

        private void RefreshAdvancedStatus()
        {
            RefreshStartupSettingFromRegistry();
            if (dashboard == null || dashboard.IsDisposed)
            {
                return;
            }

            dashboard.SetUpdateStatus(
                loadedSettings.AutomaticUpdateEnabled
                    ? "启动时自动检查已启用；只读取版本清单，不自动下载或安装。"
                    : "自动检查已关闭；手动检查会逐次确认。" );
            dashboard.SetTelemetryStatus(
                loadedSettings.TelemetryEnabled
                    ? "可发送状态已启用；每次发送仍需预览并明确确认。"
                    : "已关闭；不会发送任何遥测或诊断摘要。" );

            if (loopbackApiServer.IsRunning && loopbackApiSession != null)
            {
                dashboard.SetApiStatus(
                    "地址：http://127.0.0.1:" +
                    loopbackApiSession.Port.ToString(CultureInfo.InvariantCulture) +
                    "/v1/status\r\nAuthorization: Bearer " +
                    loopbackApiSession.BearerToken);
            }
            else
            {
                dashboard.SetApiStatus("已关闭；没有监听任何端口。");
            }
        }

        private string BuildLoopbackStatusJson()
        {
            return BuildLoopbackStatusJson(ReadLatestSnapshot());
        }

        internal static string BuildLoopbackStatusJson(HardwareSnapshot snapshot)
        {
            StringBuilder json = new StringBuilder(320);
            json.Append('{');
            bool first = true;
            long? unixMilliseconds = snapshot == null
                ? (long?)null
                : ToUnixMilliseconds(snapshot.SampledAtUtc);
            AppendJsonNumber(json, "schemaVersion", 2, ref first);
            AppendJsonNullableNumber(
                json,
                "sampledAtUnixMilliseconds",
                unixMilliseconds,
                ref first);
            AppendJsonNullableNumber(json, "cpuPercent", snapshot == null ? null : snapshot.CpuPercent, ref first);
            AppendJsonNullableNumber(json, "memoryPercent", snapshot == null ? null : snapshot.MemoryPercent, ref first);
            AppendJsonNullableNumber(json, "discreteGpuPercent", snapshot == null ? null : snapshot.GpuPercent, ref first);
            AppendJsonNullableNumber(json, "discreteMemoryPercent", snapshot == null ? null : snapshot.VideoMemoryPercent, ref first);
            AppendJsonNullableNumber(json, "discreteTemperatureCelsius", snapshot == null ? null : snapshot.TemperatureCelsius, ref first);
            AppendJsonNullableNumber(json, "integratedGpuPercent", snapshot == null ? null : snapshot.IntegratedGpuPercent, ref first);
            AppendJsonNullableNumber(json, "integratedSharedMemoryBytes", snapshot == null ? null : snapshot.IntegratedSharedMemoryBytes, ref first);
            AppendJsonNullableNumber(
                json,
                "discreteGpuVendorId",
                snapshot == null || !snapshot.DiscreteGpuVendorId.HasValue
                    ? (long?)null
                    : snapshot.DiscreteGpuVendorId.Value,
                ref first);
            AppendJsonNullableNumber(
                json,
                "integratedGpuVendorId",
                snapshot == null || !snapshot.IntegratedGpuVendorId.HasValue
                    ? (long?)null
                    : snapshot.IntegratedGpuVendorId.Value,
                ref first);
            AppendJsonNullableNumber(json, "networkReceiveBytesPerSecond", snapshot == null ? null : snapshot.NetworkReceiveBytesPerSecond, ref first);
            AppendJsonNullableNumber(json, "networkSendBytesPerSecond", snapshot == null ? null : snapshot.NetworkSendBytesPerSecond, ref first);
            AppendJsonNullableNumber(json, "networkLinkSpeedBitsPerSecond", snapshot == null ? null : snapshot.NetworkLinkSpeedBitsPerSecond, ref first);
            AppendJsonNullableNumber(json, "gatewayLatencyMilliseconds", snapshot == null ? null : snapshot.GatewayLatencyMilliseconds, ref first);
            AppendJsonNumber(json, "discreteGpuMode", snapshot == null ? -1 : (int)snapshot.GpuMode, ref first);
            AppendJsonNumber(
                json,
                "networkSelectionStatus",
                snapshot == null
                    ? (int)NetworkSelectionStatus.NoRoute
                    : (int)snapshot.NetworkSelectionStatus,
                ref first);
            AppendJsonNumber(json, "gatewayLatencyStatus", snapshot == null ? -1 : (int)snapshot.GatewayLatencyStatus, ref first);
            AppendJsonNumber(json, "integratedAdapterStatus", snapshot == null ? -1 : (int)snapshot.IntegratedAdapterStatus, ref first);

            bool isNvidiaDiscrete = snapshot != null &&
                snapshot.DiscreteGpuVendorId.HasValue &&
                snapshot.DiscreteGpuVendorId.Value == GpuRoleSampler.NvidiaVendorId;
            bool isIntelIntegrated = snapshot != null &&
                snapshot.IntegratedGpuVendorId.HasValue &&
                snapshot.IntegratedGpuVendorId.Value == GpuRoleSampler.IntelVendorId;
            AppendJsonNullableNumber(
                json,
                "nvidiaGpuPercent",
                isNvidiaDiscrete ? snapshot.GpuPercent : null,
                ref first);
            AppendJsonNullableNumber(
                json,
                "nvidiaMemoryPercent",
                isNvidiaDiscrete ? snapshot.VideoMemoryPercent : null,
                ref first);
            AppendJsonNullableNumber(
                json,
                "nvidiaTemperatureCelsius",
                isNvidiaDiscrete ? snapshot.TemperatureCelsius : null,
                ref first);
            AppendJsonNullableNumber(
                json,
                "intelGpuPercent",
                isIntelIntegrated ? snapshot.IntegratedGpuPercent : null,
                ref first);
            AppendJsonNullableNumber(
                json,
                "intelSharedMemoryBytes",
                isIntelIntegrated ? snapshot.IntegratedSharedMemoryBytes : null,
                ref first);
            AppendJsonNumber(
                json,
                "nvidiaMode",
                isNvidiaDiscrete ? (int)snapshot.GpuMode : -1,
                ref first);
            AppendJsonNumber(
                json,
                "intelAdapterStatus",
                isIntelIntegrated ? (int)snapshot.IntegratedAdapterStatus : -1,
                ref first);
            json.Append('}');
            return json.ToString();
        }

        private HardwareSnapshot ReadLatestSnapshot()
        {
            return Interlocked.CompareExchange(
                ref latestSnapshot,
                null,
                null);
        }

        private static long ToUnixMilliseconds(DateTime value)
        {
            DateTime utcValue = value.Kind == DateTimeKind.Utc
                ? value
                : value.ToUniversalTime();
            return (utcValue.Ticks - UnixEpochUtc.Ticks) /
                TimeSpan.TicksPerMillisecond;
        }

        private static void AppendJsonNullableNumber(
            StringBuilder json,
            string name,
            int? value,
            ref bool first)
        {
            AppendJsonPropertyName(json, name, ref first);
            json.Append(
                value.HasValue
                    ? value.Value.ToString(CultureInfo.InvariantCulture)
                    : "null");
        }

        private static void AppendJsonNullableNumber(
            StringBuilder json,
            string name,
            long? value,
            ref bool first)
        {
            AppendJsonPropertyName(json, name, ref first);
            json.Append(
                value.HasValue
                    ? value.Value.ToString(CultureInfo.InvariantCulture)
                    : "null");
        }

        private static void AppendJsonNumber(
            StringBuilder json,
            string name,
            long value,
            ref bool first)
        {
            AppendJsonPropertyName(json, name, ref first);
            json.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendJsonPropertyName(
            StringBuilder json,
            string name,
            ref bool first)
        {
            if (!first)
            {
                json.Append(',');
            }

            first = false;
            json.Append('"');
            json.Append(name);
            json.Append("\":");
        }

        private void ShowAdvancedOperationError(string title, Exception exception)
        {
            MessageBox.Show(
                this,
                title + "。未完成任何计划外操作。\r\n\r\n" + exception.Message,
                "TinyHwBar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void ToggleLocked(object sender, EventArgs e)
        {
            locked = !locked;
            SaveSettings(true);
        }

        private void ToggleClickThrough(object sender, EventArgs e)
        {
            clickThrough = !clickThrough;
            ApplyClickThroughStyle();
            SaveSettings(true);
        }

        private void SetOpacity(object sender, EventArgs e)
        {
            ToolStripMenuItem optionMenuItem = sender as ToolStripMenuItem;
            if (optionMenuItem == null || !(optionMenuItem.Tag is int))
            {
                return;
            }

            opacityPercent = (int)optionMenuItem.Tag;
            Opacity = opacityPercent / 100.0;
            SaveSettings(true);
        }

        private void ApplyClickThroughStyle()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            try
            {
                long extendedStyle = NativeMethods.GetWindowLongPtr(
                    Handle,
                    NativeMethods.GwlExStyle).ToInt64();

                if (clickThrough)
                {
                    extendedStyle |= NativeMethods.WsExTransparent;
                }
                else
                {
                    extendedStyle &= ~NativeMethods.WsExTransparent;
                }

                NativeMethods.SetWindowLongPtr(
                    Handle,
                    NativeMethods.GwlExStyle,
                    new IntPtr(extendedStyle));
                NativeMethods.SetWindowPos(
                    Handle,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SwpNoMove |
                    NativeMethods.SwpNoSize |
                    NativeMethods.SwpNoZOrder |
                    NativeMethods.SwpNoActivate |
                    NativeMethods.SwpFrameChanged);
            }
            catch (Exception)
            {
                RecreateHandle();
            }
        }

        private void ResetPosition(object sender, EventArgs e)
        {
            SetDefaultPosition();
            SaveSettings(true);
        }

        private void ExitApplication(object sender, EventArgs e)
        {
            Close();
        }

        private static Icon LoadApplicationIcon()
        {
            try
            {
                Icon extractedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extractedIcon != null)
                {
                    return extractedIcon;
                }
            }
            catch (Exception)
            {
                // Fall back to the standard icon if the executable resource cannot be read.
            }

            return (Icon)SystemIcons.Application.Clone();
        }

        private void SaveSettings()
        {
            SaveSettings(false);
        }

        private void SaveSettings(bool warnOnFailure)
        {
            loadedSettings.HasSavedPosition = true;
            loadedSettings.Left = Left;
            loadedSettings.Top = Top;
            loadedSettings.Locked = locked;
            loadedSettings.ClickThrough = clickThrough;
            loadedSettings.OpacityPercent = opacityPercent;
            bool saved = SettingsStore.Save(loadedSettings.Clone());

            if (dashboard != null && !dashboard.IsDisposed)
            {
                dashboard.RefreshSettings(loadedSettings.Clone());
            }

            if (!saved && warnOnFailure && !settingsPersistenceWarningShown)
            {
                settingsPersistenceWarningShown = true;
                MessageBox.Show(
                    this,
                    "设置已应用到当前会话，但无法写入本地设置文件；下次启动可能恢复旧值。\r\n\r\n请检查 %LOCALAPPDATA%\\TinyHwBar 的写入权限和可用空间。",
                    "TinyHwBar 设置未保存",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void SaveHistoryIfEnabled()
        {
            HistorySaveFailure failure = SaveHistoryCore(false);
            if (failure != null)
            {
                WarnHistoryPersistenceFailure(failure);
            }
        }

        private HistorySaveFailure SaveHistoryCore(bool allowWhileStopping)
        {
            HistorySaveFailure failure = null;
            lock (historyPersistenceSynchronization)
            {
                if (!allowWhileStopping &&
                    Interlocked.CompareExchange(ref stopping, 0, 0) != 0)
                {
                    return null;
                }

                if (!loadedSettings.PersistHistory)
                {
                    return null;
                }

                HistoryPoint[] points = history.Snapshot();
                if (historyPersistenceStartUtc != DateTime.MinValue)
                {
                    List<HistoryPoint> filteredPoints = new List<HistoryPoint>(points.Length);
                    foreach (HistoryPoint point in points)
                    {
                        if (point != null && point.TimestampUtc >= historyPersistenceStartUtc)
                        {
                            filteredPoints.Add(point);
                        }
                    }

                    points = filteredPoints.ToArray();
                }

                if (historyStore.Save(points, out failure))
                {
                    return null;
                }
            }

            return failure ?? new HistorySaveFailure(
                HistorySaveFailureStage.None,
                "Unknown",
                null);
        }

        private void WarnHistoryPersistenceFailure(HistorySaveFailure failure)
        {
            if (Interlocked.CompareExchange(ref stopping, 0, 0) != 0 ||
                !IsHandleCreated ||
                IsDisposed ||
                Interlocked.CompareExchange(
                    ref historyPersistenceWarningScheduled,
                    1,
                    0) != 0)
            {
                return;
            }

            string failureText = failure == null
                ? "失败阶段：保存本地历史；错误类型：Unknown"
                : failure.ToSafeDisplayText();
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        ShowHistoryPersistenceWarning(failureText, false);
                    });
                }
                catch (ObjectDisposedException)
                {
                    // The form is closing.
                    ResetHistoryWarningScheduleIfActive();
                }
                catch (InvalidOperationException)
                {
                    // The form is closing.
                    ResetHistoryWarningScheduleIfActive();
                }

                return;
            }

            ShowHistoryPersistenceWarning(failureText, false);
        }

        private void ResetHistoryWarningScheduleIfActive()
        {
            if (Interlocked.CompareExchange(ref stopping, 0, 0) == 0 &&
                !IsDisposed)
            {
                Interlocked.Exchange(
                    ref historyPersistenceWarningScheduled,
                    0);
            }
        }

        private void ShowHistoryPersistenceWarning(
            string failureText,
            bool allowWhileStopping)
        {
            if ((!allowWhileStopping &&
                 Interlocked.CompareExchange(ref stopping, 0, 0) != 0) ||
                IsDisposed)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref historyPersistenceWarningShown,
                    1,
                    0) != 0)
            {
                return;
            }

            MessageBox.Show(
                this,
                "历史仍保留在当前会话内，但无法写入本地历史文件；退出后这些新记录可能丢失。\r\n\r\n" +
                failureText +
                "\r\n\r\n请检查 %LOCALAPPDATA%\\TinyHwBar 的写入权限和可用空间。",
                "TinyHwBar 历史未保存",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
