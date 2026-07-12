using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
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

        private readonly AppSettings loadedSettings;
        private readonly HardwareSampler sampler;
        private readonly Font primaryFont;
        private readonly Font compactFont;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem visibilityMenuItem;
        private readonly ToolStripMenuItem lockMenuItem;
        private readonly ToolStripMenuItem clickThroughMenuItem;
        private readonly NotifyIcon notifyIcon;

        private System.Threading.Timer sampleTimer;
        private int sampleBusy;
        private int stopping;
        private bool locked;
        private bool clickThrough;
        private bool dragging;
        private Point dragStartCursor;
        private Point dragStartLocation;
        private string statusText = "CPU -- · RAM -- · GPU -- · VR -- · --°";

        internal MonitorForm()
        {
            loadedSettings = SettingsStore.Load();
            locked = loadedSettings.Locked;
            clickThrough = loadedSettings.ClickThrough;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(0x20, 0x24, 0x2A);
            ForeColor = Color.FromArgb(0xF0, 0xF3, 0xF6);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "TinyHwBar";
            AccessibleName = "TinyHwBar hardware monitor";
            TopMost = true;
            Opacity = 0.90;
            ClientSize = new Size(BarWidth, BarHeight);
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
            ToolStripMenuItem resetMenuItem = new ToolStripMenuItem("重置到右上角");
            ToolStripMenuItem exitMenuItem = new ToolStripMenuItem("退出");

            visibilityMenuItem.Click += ToggleVisibility;
            lockMenuItem.Click += ToggleLocked;
            clickThroughMenuItem.Click += ToggleClickThrough;
            resetMenuItem.Click += ResetPosition;
            exitMenuItem.Click += ExitApplication;

            menu.Items.Add(visibilityMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(lockMenuItem);
            menu.Items.Add(clickThroughMenuItem);
            menu.Items.Add(resetMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitMenuItem);
            menu.Opening += UpdateMenuState;
            ContextMenuStrip = menu;

            notifyIcon = new NotifyIcon();
            notifyIcon.Text = "TinyHwBar";
            notifyIcon.Icon = SystemIcons.Application;
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.DoubleClick += ToggleVisibility;
            notifyIcon.Visible = true;

            sampler = new HardwareSampler();
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

            sampleTimer = new System.Threading.Timer(
                SampleHardware,
                null,
                0,
                SampleIntervalMilliseconds);

            ApplyClickThroughStyle();
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
            Location = new Point(
                dragStartLocation.X + cursor.X - dragStartCursor.X,
                dragStartLocation.Y + cursor.Y - dragStartCursor.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Left && dragging)
            {
                dragging = false;
                Capture = false;
                CorrectPositionForCurrentScreens();
                SaveSettings();
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
                CorrectPositionForCurrentScreens();
                SaveSettings();
                return;
            }

            if (message.Msg == NativeMethods.WmDisplayChange)
            {
                base.WndProc(ref message);
                CorrectPositionForCurrentScreens();
                SaveSettings();
                return;
            }

            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Interlocked.Exchange(ref stopping, 1);

            if (sampleTimer != null)
            {
                sampleTimer.Dispose();
                sampleTimer = null;
            }

            sampler.Dispose();
            SaveSettings();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            menu.Dispose();
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
                            statusText = newStatusText;
                            Invalidate();
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

        private void ApplyInitialPosition()
        {
            Rectangle savedBounds = new Rectangle(
                loadedSettings.Left,
                loadedSettings.Top,
                BarWidth,
                BarHeight);

            if (loadedSettings.HasSavedPosition && IsFullyInsideAnyWorkingArea(savedBounds))
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

        private static bool IsFullyInsideAnyWorkingArea(Rectangle bounds)
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.Contains(bounds))
                {
                    return true;
                }
            }

            return false;
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
                if (screen.WorkingArea.IntersectsWith(Bounds))
                {
                    intersectsAnyScreen = true;
                    break;
                }
            }

            if (!intersectsAnyScreen)
            {
                SetDefaultPosition();
                return;
            }

            Rectangle workingArea = Screen.FromRectangle(Bounds).WorkingArea;
            int maximumLeft = Math.Max(workingArea.Left, workingArea.Right - BarWidth);
            int maximumTop = Math.Max(workingArea.Top, workingArea.Bottom - BarHeight);
            int correctedLeft = Math.Min(Math.Max(Left, workingArea.Left), maximumLeft);
            int correctedTop = Math.Min(Math.Max(Top, workingArea.Top), maximumTop);

            SetBounds(
                correctedLeft,
                correctedTop,
                BarWidth,
                BarHeight,
                BoundsSpecified.All);
        }

        private void UpdateMenuState(object sender, CancelEventArgs e)
        {
            visibilityMenuItem.Text = Visible ? "隐藏监控条" : "显示监控条";
            lockMenuItem.Checked = locked;
            clickThroughMenuItem.Checked = clickThrough;
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

        private void ToggleLocked(object sender, EventArgs e)
        {
            locked = !locked;
            SaveSettings();
        }

        private void ToggleClickThrough(object sender, EventArgs e)
        {
            clickThrough = !clickThrough;
            ApplyClickThroughStyle();
            SaveSettings();
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
            SaveSettings();
        }

        private void ExitApplication(object sender, EventArgs e)
        {
            Close();
        }

        private void SaveSettings()
        {
            SettingsStore.Save(Left, Top, locked, clickThrough);
        }
    }
}
