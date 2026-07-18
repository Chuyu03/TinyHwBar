using System;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace TinyHwBar
{
    internal sealed class GrayscaleTextCheckBox : CheckBox
    {
        private bool grayscaleTextRenderingEnabled;

        internal GrayscaleTextCheckBox()
        {
            UseCompatibleTextRendering = false;
        }

        internal void EnableGrayscaleTextRendering()
        {
            if (grayscaleTextRenderingEnabled)
            {
                return;
            }

            Size originalPreferredSize = GetPreferredSize(Size.Empty);
            MinimumSize = originalPreferredSize;
            MaximumSize = originalPreferredSize;
            UseCompatibleTextRendering = true;
            grayscaleTextRenderingEnabled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderingHint previousHint = e.Graphics.TextRenderingHint;
            try
            {
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                base.OnPaint(e);
            }
            finally
            {
                e.Graphics.TextRenderingHint = previousHint;
            }
        }
    }

    internal sealed class GrayscaleTextLabel : Label
    {
        private bool grayscaleTextRenderingEnabled;

        internal GrayscaleTextLabel()
        {
            UseCompatibleTextRendering = false;
        }

        internal void EnableGrayscaleTextRendering()
        {
            if (grayscaleTextRenderingEnabled)
            {
                return;
            }

            int originalPreferredHeight = GetPreferredSize(Size.Empty).Height;
            MinimumSize = new Size(MinimumSize.Width, originalPreferredHeight);
            MaximumSize = new Size(MaximumSize.Width, originalPreferredHeight);
            UseCompatibleTextRendering = true;
            grayscaleTextRenderingEnabled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderingHint previousHint = e.Graphics.TextRenderingHint;
            try
            {
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                base.OnPaint(e);
            }
            finally
            {
                e.Graphics.TextRenderingHint = previousHint;
            }
        }
    }

    [Flags]
    internal enum DashboardDirtyFields
    {
        None = 0,
        Opacity = 1,
        Locked = 2,
        ClickThrough = 4,
        History = 8,
        Gateway = 16,
        Gpu = 32,
        Startup = 64,
        Update = 128,
        LoopbackApi = 256,
        Telemetry = 512
    }

    internal sealed class DashboardSettingsChangedEventArgs : EventArgs
    {
        internal DashboardSettingsChangedEventArgs(
            bool locked,
            bool clickThrough,
            int opacityPercent,
            bool persistHistory,
            bool gatewayLatencyEnabled,
            GpuSelectionMode gpuSelectionMode,
            string selectedGpuAdapterId,
            string selectedGpuAdapterName,
            bool startupEnabled,
            bool automaticUpdateEnabled,
            string updateManifestUrl,
            bool loopbackApiEnabled,
            bool telemetryEnabled,
            string telemetryEndpoint)
        {
            Locked = locked;
            ClickThrough = clickThrough;
            OpacityPercent = opacityPercent;
            PersistHistory = persistHistory;
            GatewayLatencyEnabled = gatewayLatencyEnabled;
            GpuSelectionMode = gpuSelectionMode;
            SelectedGpuAdapterId = selectedGpuAdapterId ?? string.Empty;
            SelectedGpuAdapterName = selectedGpuAdapterName ?? string.Empty;
            StartupEnabled = startupEnabled;
            AutomaticUpdateEnabled = automaticUpdateEnabled;
            UpdateManifestUrl = updateManifestUrl ?? string.Empty;
            LoopbackApiEnabled = loopbackApiEnabled;
            TelemetryEnabled = telemetryEnabled;
            TelemetryEndpoint = telemetryEndpoint ?? string.Empty;
        }

        internal bool Locked { get; private set; }

        internal bool ClickThrough { get; private set; }

        internal int OpacityPercent { get; private set; }

        internal bool PersistHistory { get; private set; }

        internal bool GatewayLatencyEnabled { get; private set; }

        internal GpuSelectionMode GpuSelectionMode { get; private set; }

        internal string SelectedGpuAdapterId { get; private set; }

        internal string SelectedGpuAdapterName { get; private set; }

        internal bool StartupEnabled { get; private set; }

        internal bool AutomaticUpdateEnabled { get; private set; }

        internal string UpdateManifestUrl { get; private set; }

        internal bool LoopbackApiEnabled { get; private set; }

        internal bool TelemetryEnabled { get; private set; }

        internal string TelemetryEndpoint { get; private set; }
    }

    internal sealed class DashboardEndpointActionEventArgs : EventArgs
    {
        internal DashboardEndpointActionEventArgs(bool enabled, string endpoint)
        {
            Enabled = enabled;
            Endpoint = endpoint == null ? string.Empty : endpoint.Trim();
        }

        internal bool Enabled { get; private set; }

        internal string Endpoint { get; private set; }
    }

    internal sealed class DashboardForm : Form
    {
        private readonly MetricHistory history;
        private readonly HistoryChartControl historyChart;
        private readonly Label cpuValue;
        private readonly Label memoryValue;
        private readonly Label discreteGpuValue;
        private readonly Label discreteGpuName;
        private readonly Label integratedGpuValue;
        private readonly Label integratedGpuName;
        private readonly Label receiveValue;
        private readonly Label sendValue;
        private readonly Label linkValue;
        private readonly Label adapterValue;
        private readonly Label gatewayLatencyValue;
        private readonly Label gatewayValue;
        private readonly ComboBox opacityComboBox;
        private readonly CheckBox lockedCheckBox;
        private readonly CheckBox clickThroughCheckBox;
        private readonly CheckBox persistHistoryCheckBox;
        private readonly CheckBox gatewayLatencyCheckBox;
        private readonly RadioButton automaticGpuRadioButton;
        private readonly RadioButton manualGpuRadioButton;
        private readonly ComboBox gpuAdapterComboBox;
        private readonly Button refreshGpuAdaptersButton;
        private readonly Label gpuSelectionStatusLabel;
        private readonly CheckBox startupCheckBox;
        private readonly CheckBox automaticUpdateCheckBox;
        private readonly TextBox updateEndpointTextBox;
        private readonly CheckBox loopbackApiCheckBox;
        private readonly CheckBox telemetryCheckBox;
        private readonly TextBox telemetryEndpointTextBox;
        private readonly Label startupStatusLabel;
        private readonly Label updateStatusLabel;
        private readonly TextBox apiStatusTextBox;
        private readonly Label telemetryStatusLabel;
        private readonly Icon applicationIcon;
        private readonly Font uiFont;
        private readonly Font metricFont;
        private bool resourcesDisposed;
        private bool synchronizingSettings;
        private DashboardDirtyFields dirtyFields;
        private HardwareSnapshot latestSnapshot;

        internal DashboardForm(AppSettings settings, MetricHistory metricHistory)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if (metricHistory == null)
            {
                throw new ArgumentNullException("metricHistory");
            }

            history = metricHistory;
            applicationIcon = LoadApplicationIcon();
            uiFont = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
            metricFont = new Font(
                "Segoe UI Semibold",
                15.0f,
                FontStyle.Regular,
                GraphicsUnit.Point);

            Text = "TinyHwBar 控制中心";
            Icon = applicationIcon;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(248, 250, 252);
            ForeColor = Color.FromArgb(31, 41, 55);
            Font = uiFont;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(680, 530);
            ClientSize = new Size(760, 560);
            MaximizeBox = true;
            MinimizeBox = true;
            ShowInTaskbar = true;
            AccessibleName = "TinyHwBar control center";

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(16, 6);
            tabs.TabIndex = 0;

            TabPage overviewPage = new TabPage("概览");
            overviewPage.BackColor = BackColor;
            overviewPage.Padding = new Padding(10);

            TableLayoutPanel overviewGrid = new TableLayoutPanel();
            overviewGrid.Dock = DockStyle.Fill;
            overviewGrid.ColumnCount = 2;
            overviewGrid.RowCount = 5;
            overviewGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0f));
            overviewGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.0f));
            for (int row = 0; row < 5; row++)
            {
                overviewGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 20.0f));
            }

            cpuValue = CreateMetricCard(overviewGrid, "CPU", 0, 0);
            memoryValue = CreateMetricCard(overviewGrid, "物理内存", 1, 0);
            discreteGpuValue = CreateGpuMetricCard(
                overviewGrid,
                "独立显卡",
                0,
                1,
                out discreteGpuName);
            integratedGpuValue = CreateGpuMetricCard(
                overviewGrid,
                "核显",
                1,
                1,
                out integratedGpuName);
            receiveValue = CreateMetricCard(overviewGrid, "网络下载", 0, 2);
            sendValue = CreateMetricCard(overviewGrid, "网络上传", 1, 2);
            linkValue = CreateMetricCard(overviewGrid, "适配器链路速率", 0, 3);
            adapterValue = CreateMetricCard(overviewGrid, "监测网络适配器", 1, 3);
            gatewayLatencyValue = CreateMetricCard(overviewGrid, "所选接口网关延迟", 0, 4);
            gatewayValue = CreateMetricCard(overviewGrid, "所选接口网关", 1, 4);
            overviewPage.Controls.Add(overviewGrid);

            TabPage historyPage = new TabPage("历史曲线");
            historyPage.BackColor = BackColor;
            historyPage.Padding = new Padding(10);

            historyChart = new HistoryChartControl();
            historyChart.Dock = DockStyle.Fill;
            historyChart.SetPoints(history.Snapshot());

            Button clearHistoryButton = new Button();
            clearHistoryButton.Text = "清除全部本地历史";
            clearHistoryButton.AutoSize = true;
            clearHistoryButton.Anchor = AnchorStyles.Right;
            clearHistoryButton.TabIndex = 0;
            clearHistoryButton.AccessibleDescription =
                "清除 TinyHwBar 保存在本机的全部数值历史记录。";
            clearHistoryButton.Click += ClearHistory;

            TableLayoutPanel historyLayout = new TableLayoutPanel();
            historyLayout.Dock = DockStyle.Fill;
            historyLayout.ColumnCount = 1;
            historyLayout.RowCount = 2;
            historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));
            historyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            historyLayout.Controls.Add(historyChart, 0, 0);

            FlowLayoutPanel historyActions = new FlowLayoutPanel();
            historyActions.Dock = DockStyle.Fill;
            historyActions.FlowDirection = FlowDirection.RightToLeft;
            historyActions.AutoSize = true;
            historyActions.Padding = new Padding(0, 8, 0, 0);
            historyActions.TabIndex = 0;
            historyActions.Controls.Add(clearHistoryButton);
            historyLayout.Controls.Add(historyActions, 0, 1);
            historyPage.Controls.Add(historyLayout);

            TabPage settingsPage = new TabPage("设置");
            settingsPage.BackColor = BackColor;
            settingsPage.Padding = new Padding(18);

            TableLayoutPanel settingsLayout = new TableLayoutPanel();
            settingsLayout.Dock = DockStyle.Fill;
            settingsLayout.AutoScroll = true;
            settingsLayout.ColumnCount = 2;
            settingsLayout.RowCount = 9;
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150.0f));
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label opacityLabel = new Label();
            opacityLabel.Text = "监控条透明度";
            opacityLabel.AutoSize = true;
            opacityLabel.Anchor = AnchorStyles.Left;
            opacityLabel.Margin = new Padding(0, 8, 8, 8);

            opacityComboBox = new ComboBox();
            opacityComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            opacityComboBox.Width = 120;
            opacityComboBox.Margin = new Padding(0, 4, 0, 8);
            opacityComboBox.TabIndex = 0;
            opacityComboBox.AccessibleName = "监控条透明度";
            foreach (int opacity in AppSettings.SupportedOpacityPercentages)
            {
                opacityComboBox.Items.Add(opacity.ToString(CultureInfo.InvariantCulture) + "%");
            }

            lockedCheckBox = new CheckBox();
            lockedCheckBox.Text = "锁定监控条位置";
            lockedCheckBox.AutoSize = true;
            lockedCheckBox.Margin = new Padding(0, 8, 0, 8);
            lockedCheckBox.TabIndex = 1;

            clickThroughCheckBox = new CheckBox();
            clickThroughCheckBox.Text = "启用鼠标穿透";
            clickThroughCheckBox.AutoSize = true;
            clickThroughCheckBox.Margin = new Padding(0, 8, 0, 8);
            clickThroughCheckBox.TabIndex = 2;

            persistHistoryCheckBox = new CheckBox();
            persistHistoryCheckBox.Text = "保存最近 900 个指标点到本机";
            persistHistoryCheckBox.AutoSize = true;
            persistHistoryCheckBox.Margin = new Padding(0, 8, 0, 8);
            persistHistoryCheckBox.TabIndex = 3;
            persistHistoryCheckBox.AccessibleDescription =
                "仅保存 TinyHwBar 显示的数值指标，不保存应用、网址或用户内容。";

            gatewayLatencyCheckBox = new CheckBox();
            gatewayLatencyCheckBox.Text = "测量所选接口报告的本地网关延迟";
            gatewayLatencyCheckBox.AutoSize = true;
            gatewayLatencyCheckBox.Margin = new Padding(0, 8, 0, 8);
            gatewayLatencyCheckBox.TabIndex = 4;
            gatewayLatencyCheckBox.AccessibleDescription =
                "每 10 秒仅向当前接口报告的本地网关发送一个最多等待 750 毫秒的 ICMP 请求；" +
                "只允许 RFC1918、CGNAT、IPv4/IPv6 链路本地和 IPv6 ULA 地址，" +
                "公网或全局地址会被拒绝，不执行主动带宽测速。";

            GroupBox gpuSelectionGroup = new GroupBox();
            gpuSelectionGroup.Text = "GPU 显示选择";
            gpuSelectionGroup.Dock = DockStyle.Top;
            gpuSelectionGroup.AutoSize = true;
            gpuSelectionGroup.Padding = new Padding(12, 8, 12, 10);
            gpuSelectionGroup.Margin = new Padding(0, 8, 0, 8);

            TableLayoutPanel gpuSelectionLayout = new TableLayoutPanel();
            gpuSelectionLayout.Dock = DockStyle.Top;
            gpuSelectionLayout.AutoSize = true;
            gpuSelectionLayout.ColumnCount = 2;
            gpuSelectionLayout.RowCount = 5;
            gpuSelectionLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100.0f));
            gpuSelectionLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));
            for (int row = 0; row < 5; row++)
            {
                gpuSelectionLayout.RowStyles.Add(
                    new RowStyle(SizeType.AutoSize));
            }

            automaticGpuRadioButton = new RadioButton();
            automaticGpuRadioButton.Text = "自动选择（推荐）";
            automaticGpuRadioButton.AutoSize = true;
            automaticGpuRadioButton.TabIndex = 0;
            automaticGpuRadioButton.AccessibleName = "GPU 自动选择（推荐）";
            automaticGpuRadioButton.AccessibleDescription =
                "自动选择可用的物理显卡；独显处于省电状态或数据不可用时回退核显。";
            automaticGpuRadioButton.CheckedChanged += UpdateGpuSelectionControls;

            Label automaticGpuDescription = new Label();
            automaticGpuDescription.Text =
                "默认自动选择，无需设置；设备变化后会重新选择，不按瞬时负载来回切换。";
            automaticGpuDescription.AutoSize = true;
            automaticGpuDescription.ForeColor = Color.FromArgb(71, 85, 105);
            automaticGpuDescription.Margin = new Padding(22, 0, 0, 6);

            manualGpuRadioButton = new RadioButton();
            manualGpuRadioButton.Text = "手动指定监控条显示的 GPU";
            manualGpuRadioButton.AutoSize = true;
            manualGpuRadioButton.TabIndex = 1;
            manualGpuRadioButton.AccessibleName = "GPU 手动选择";
            manualGpuRadioButton.CheckedChanged += UpdateGpuSelectionControls;

            gpuAdapterComboBox = new ComboBox();
            gpuAdapterComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            gpuAdapterComboBox.Dock = DockStyle.Fill;
            gpuAdapterComboBox.Margin = new Padding(22, 4, 8, 4);
            gpuAdapterComboBox.TabIndex = 2;
            gpuAdapterComboBox.AccessibleName = "手动选择 GPU 设备";
            gpuAdapterComboBox.AccessibleDescription =
                "列出本机检测到的物理独立显卡和核显。自动模式下禁用。";
            gpuAdapterComboBox.SelectedIndexChanged += UpdateGpuSelectionStatus;

            refreshGpuAdaptersButton = new Button();
            refreshGpuAdaptersButton.Text = "刷新设备";
            refreshGpuAdaptersButton.AutoSize = true;
            refreshGpuAdaptersButton.Margin = new Padding(0, 3, 0, 3);
            refreshGpuAdaptersButton.TabIndex = 3;
            refreshGpuAdaptersButton.AccessibleName = "刷新 GPU 设备列表";
            refreshGpuAdaptersButton.Click += RefreshGpuAdapters;

            gpuSelectionStatusLabel = new Label();
            gpuSelectionStatusLabel.AutoSize = true;
            gpuSelectionStatusLabel.Dock = DockStyle.Fill;
            gpuSelectionStatusLabel.ForeColor = Color.FromArgb(30, 64, 175);
            gpuSelectionStatusLabel.Margin = new Padding(22, 4, 0, 0);
            gpuSelectionStatusLabel.AccessibleName = "GPU 选择状态";

            gpuSelectionLayout.Controls.Add(automaticGpuRadioButton, 0, 0);
            gpuSelectionLayout.SetColumnSpan(automaticGpuRadioButton, 2);
            gpuSelectionLayout.Controls.Add(automaticGpuDescription, 0, 1);
            gpuSelectionLayout.SetColumnSpan(automaticGpuDescription, 2);
            gpuSelectionLayout.Controls.Add(manualGpuRadioButton, 0, 2);
            gpuSelectionLayout.SetColumnSpan(manualGpuRadioButton, 2);
            gpuSelectionLayout.Controls.Add(gpuAdapterComboBox, 0, 3);
            gpuSelectionLayout.Controls.Add(refreshGpuAdaptersButton, 1, 3);
            gpuSelectionLayout.Controls.Add(gpuSelectionStatusLabel, 0, 4);
            gpuSelectionLayout.SetColumnSpan(gpuSelectionStatusLabel, 2);
            gpuSelectionGroup.Controls.Add(gpuSelectionLayout);

            Label privacyNote = new Label();
            privacyNote.AutoSize = false;
            privacyNote.Dock = DockStyle.Fill;
            privacyNote.Padding = new Padding(12);
            privacyNote.BackColor = Color.FromArgb(239, 246, 255);
            privacyNote.ForeColor = Color.FromArgb(30, 64, 175);
            privacyNote.Text =
                "TinyHwBar 默认自动选择 GPU，无需手动设置；本地历史和本地网关检测都可在本页关闭。\r\n\r\n" +
                "历史仅在本机保存最近 900 个数值点；超过 24 小时的记录会被忽略，格式异常的文件会保留且不会自动覆盖，可随时确认清除。\r\n\r\n" +
                "网关延迟只测当前接口报告的本地网关：允许 RFC1918、CGNAT、IPv4/IPv6 链路本地和 IPv6 ULA；" +
                "公网或全局地址会被拒绝，不执行主动带宽测速。";

            Button applyButton = new Button();
            applyButton.Text = "应用";
            applyButton.AutoSize = true;
            applyButton.TabIndex = 0;
            applyButton.Click += ApplySettings;

            Button closeButton = new Button();
            closeButton.Text = "关闭";
            closeButton.AutoSize = true;
            closeButton.TabIndex = 1;
            closeButton.DialogResult = DialogResult.Cancel;
            closeButton.Click += CloseWindow;

            FlowLayoutPanel settingsActions = new FlowLayoutPanel();
            settingsActions.Dock = DockStyle.Fill;
            settingsActions.FlowDirection = FlowDirection.RightToLeft;
            settingsActions.AutoSize = true;
            settingsActions.TabIndex = 5;
            settingsActions.Controls.Add(closeButton);
            settingsActions.Controls.Add(applyButton);

            settingsLayout.Controls.Add(opacityLabel, 0, 0);
            settingsLayout.Controls.Add(opacityComboBox, 1, 0);
            settingsLayout.Controls.Add(lockedCheckBox, 1, 1);
            settingsLayout.Controls.Add(clickThroughCheckBox, 1, 2);
            settingsLayout.Controls.Add(persistHistoryCheckBox, 1, 3);
            settingsLayout.Controls.Add(gatewayLatencyCheckBox, 1, 4);
            settingsLayout.Controls.Add(gpuSelectionGroup, 0, 5);
            settingsLayout.SetColumnSpan(gpuSelectionGroup, 2);
            settingsLayout.Controls.Add(privacyNote, 0, 6);
            settingsLayout.SetColumnSpan(privacyNote, 2);
            settingsLayout.Controls.Add(settingsActions, 0, 8);
            settingsLayout.SetColumnSpan(settingsActions, 2);
            settingsPage.Controls.Add(settingsLayout);

            TabPage advancedPage = new TabPage("高级功能");
            advancedPage.BackColor = BackColor;
            advancedPage.Padding = new Padding(18);

            TableLayoutPanel advancedLayout = new TableLayoutPanel();
            advancedLayout.Dock = DockStyle.Fill;
            advancedLayout.AutoScroll = true;
            advancedLayout.ColumnCount = 2;
            advancedLayout.RowCount = 14;
            advancedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170.0f));
            advancedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            for (int row = 0; row < 14; row++)
            {
                advancedLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            Label advancedIntro = CreateWrappedNote(
                "以下能力都默认关闭。勾选并应用后，TinyHwBar 才会写入当前用户启动项、启用自动更新、启动本机 loopback 接口或允许遥测。手动检查更新可使用当前填写的地址，但每次都会先单独确认，不会静默联网。",
                Color.FromArgb(255, 247, 237),
                Color.FromArgb(154, 52, 18));
            advancedLayout.Controls.Add(advancedIntro, 0, 0);
            advancedLayout.SetColumnSpan(advancedIntro, 2);

            startupCheckBox = new CheckBox();
            startupCheckBox.Text = "登录 Windows 后启动 TinyHwBar";
            startupCheckBox.AutoSize = true;
            startupCheckBox.Margin = new Padding(0, 10, 0, 4);
            startupCheckBox.TabIndex = 0;
            advancedLayout.Controls.Add(startupCheckBox, 1, 1);

            startupStatusLabel = CreateStatusLabel("当前状态：未读取");
            advancedLayout.Controls.Add(startupStatusLabel, 1, 2);

            automaticUpdateCheckBox = new CheckBox();
            automaticUpdateCheckBox.Text = "启动后自动检查一次更新";
            automaticUpdateCheckBox.AutoSize = true;
            automaticUpdateCheckBox.Margin = new Padding(0, 10, 0, 4);
            automaticUpdateCheckBox.TabIndex = 1;
            automaticUpdateCheckBox.AccessibleDescription =
                "显式启用后，每次启动只检查一次配置的 HTTPS 更新清单。";
            advancedLayout.Controls.Add(automaticUpdateCheckBox, 1, 3);

            Label updateEndpointLabel = CreateFieldLabel("更新清单 HTTPS 地址");
            updateEndpointTextBox = new TextBox();
            updateEndpointTextBox.Dock = DockStyle.Top;
            updateEndpointTextBox.MaxLength = 2048;
            updateEndpointTextBox.TabIndex = 2;
            updateEndpointTextBox.AccessibleName = "更新清单 HTTPS 地址";
            updateEndpointTextBox.AccessibleDescription =
                "只有手动检查或显式启用自动检查后才访问此地址。";
            advancedLayout.Controls.Add(updateEndpointLabel, 0, 4);
            advancedLayout.Controls.Add(updateEndpointTextBox, 1, 4);

            Button manualUpdateButton = new Button();
            manualUpdateButton.Text = "手动检查更新";
            manualUpdateButton.AutoSize = true;
            manualUpdateButton.TabIndex = 3;
            manualUpdateButton.Click += RequestUpdateCheck;
            advancedLayout.Controls.Add(manualUpdateButton, 1, 5);

            updateStatusLabel = CreateStatusLabel("尚未检查更新");
            advancedLayout.Controls.Add(updateStatusLabel, 1, 6);

            loopbackApiCheckBox = new CheckBox();
            loopbackApiCheckBox.Text = "启用仅限本机的状态 API";
            loopbackApiCheckBox.AutoSize = true;
            loopbackApiCheckBox.Margin = new Padding(0, 10, 0, 4);
            loopbackApiCheckBox.TabIndex = 4;
            loopbackApiCheckBox.AccessibleDescription =
                "仅绑定 127.0.0.1 随机高端口，并使用每次运行重新生成的令牌。";
            advancedLayout.Controls.Add(loopbackApiCheckBox, 1, 7);

            Label apiStatusLabel = CreateFieldLabel("本机 API 状态");
            apiStatusTextBox = new TextBox();
            apiStatusTextBox.Dock = DockStyle.Top;
            apiStatusTextBox.Multiline = true;
            apiStatusTextBox.ReadOnly = true;
            apiStatusTextBox.BackColor = Color.White;
            apiStatusTextBox.Height = 62;
            apiStatusTextBox.Text = "已关闭";
            apiStatusTextBox.TabIndex = 5;
            apiStatusTextBox.AccessibleName = "本机 API 状态";
            advancedLayout.Controls.Add(apiStatusLabel, 0, 8);
            advancedLayout.Controls.Add(apiStatusTextBox, 1, 8);

            GrayscaleTextCheckBox grayscaleTelemetryCheckBox =
                new GrayscaleTextCheckBox();
            telemetryCheckBox = grayscaleTelemetryCheckBox;
            telemetryCheckBox.Text = "允许逐次确认的遥测/诊断摘要发送";
            telemetryCheckBox.AutoSize = true;
            telemetryCheckBox.Margin = new Padding(0, 10, 0, 4);
            telemetryCheckBox.TabIndex = 6;
            telemetryCheckBox.AccessibleDescription =
                "启用后仍不会自动发送；每次发送都需要预览和再次确认。";
            advancedLayout.Controls.Add(telemetryCheckBox, 1, 9);

            Label telemetryEndpointLabel = CreateFieldLabel("遥测 HTTPS 地址");
            telemetryEndpointTextBox = new TextBox();
            telemetryEndpointTextBox.Dock = DockStyle.Top;
            telemetryEndpointTextBox.MaxLength = 2048;
            telemetryEndpointTextBox.TabIndex = 7;
            telemetryEndpointTextBox.AccessibleName = "遥测 HTTPS 地址";
            telemetryEndpointTextBox.AccessibleDescription =
                "端点默认为空；发送前会展示精确字段、目标和字节数并再次确认。";
            advancedLayout.Controls.Add(telemetryEndpointLabel, 0, 10);
            advancedLayout.Controls.Add(telemetryEndpointTextBox, 1, 10);

            FlowLayoutPanel telemetryActions = new FlowLayoutPanel();
            telemetryActions.AutoSize = true;
            telemetryActions.Dock = DockStyle.Fill;
            telemetryActions.FlowDirection = FlowDirection.LeftToRight;
            telemetryActions.TabIndex = 8;
            Button previewTelemetryButton = new Button();
            previewTelemetryButton.Text = "预览遥测字段";
            previewTelemetryButton.AutoSize = true;
            previewTelemetryButton.TabIndex = 0;
            previewTelemetryButton.Click += RequestTelemetryPreview;
            Button previewDiagnosticButton = new Button();
            previewDiagnosticButton.Text = "预览诊断摘要";
            previewDiagnosticButton.AutoSize = true;
            previewDiagnosticButton.TabIndex = 1;
            previewDiagnosticButton.Click += RequestDiagnosticPreview;
            telemetryActions.Controls.Add(previewTelemetryButton);
            telemetryActions.Controls.Add(previewDiagnosticButton);
            advancedLayout.Controls.Add(telemetryActions, 1, 11);

            GrayscaleTextLabel grayscaleTelemetryStatusLabel =
                new GrayscaleTextLabel();
            grayscaleTelemetryStatusLabel.Text = "未发送任何遥测或诊断数据";
            grayscaleTelemetryStatusLabel.AutoSize = true;
            grayscaleTelemetryStatusLabel.Dock = DockStyle.Fill;
            grayscaleTelemetryStatusLabel.ForeColor = Color.FromArgb(71, 85, 105);
            grayscaleTelemetryStatusLabel.Margin = new Padding(0, 2, 0, 8);
            telemetryStatusLabel = grayscaleTelemetryStatusLabel;
            advancedLayout.Controls.Add(telemetryStatusLabel, 1, 12);

            FlowLayoutPanel advancedActions = new FlowLayoutPanel();
            advancedActions.AutoSize = true;
            advancedActions.Dock = DockStyle.Fill;
            advancedActions.FlowDirection = FlowDirection.RightToLeft;
            Button applyAdvancedButton = new Button();
            applyAdvancedButton.Text = "应用高级设置";
            applyAdvancedButton.AutoSize = true;
            applyAdvancedButton.TabIndex = 0;
            applyAdvancedButton.Click += ApplySettings;
            advancedActions.TabIndex = 9;
            advancedActions.Controls.Add(applyAdvancedButton);
            advancedLayout.Controls.Add(advancedActions, 0, 13);
            advancedLayout.SetColumnSpan(advancedActions, 2);
            advancedPage.Controls.Add(advancedLayout);

            tabs.TabPages.Add(overviewPage);
            tabs.TabPages.Add(historyPage);
            tabs.TabPages.Add(settingsPage);
            tabs.TabPages.Add(advancedPage);
            Controls.Add(tabs);
            grayscaleTelemetryCheckBox.EnableGrayscaleTextRendering();
            grayscaleTelemetryStatusLabel.EnableGrayscaleTextRendering();

            AcceptButton = applyButton;
            CancelButton = closeButton;
            RefreshGpuAdapterChoices(
                settings.SelectedGpuAdapterId,
                settings.SelectedGpuAdapterName);
            RefreshSettings(settings);
            WireSettingsDirtyTracking();
        }

        internal event EventHandler<DashboardSettingsChangedEventArgs> SettingsApplied;

        internal event EventHandler HistoryCleared;

        internal event EventHandler<DashboardEndpointActionEventArgs> UpdateCheckRequested;

        internal event EventHandler<DashboardEndpointActionEventArgs> TelemetryPreviewRequested;

        internal event EventHandler<DashboardEndpointActionEventArgs> DiagnosticPreviewRequested;

        internal void RefreshSettings(AppSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            bool wasSynchronizing = synchronizingSettings;
            synchronizingSettings = true;
            try
            {
                SetDisplayControls(settings);
                persistHistoryCheckBox.Checked = settings.PersistHistory;
                gatewayLatencyCheckBox.Checked = settings.GatewayLatencyEnabled;
                SetGpuControls(settings);
                startupCheckBox.Checked = settings.StartupEnabled;
                automaticUpdateCheckBox.Checked = settings.AutomaticUpdateEnabled;
                updateEndpointTextBox.Text = settings.UpdateManifestUrl ?? string.Empty;
                loopbackApiCheckBox.Checked = settings.LoopbackApiEnabled;
                telemetryCheckBox.Checked = settings.TelemetryEnabled;
                telemetryEndpointTextBox.Text = settings.TelemetryEndpoint ?? string.Empty;
                dirtyFields = DashboardDirtyFields.None;
            }
            finally
            {
                synchronizingSettings = wasSynchronizing;
            }
        }

        internal void SyncSettings(AppSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            bool wasSynchronizing = synchronizingSettings;
            synchronizingSettings = true;
            try
            {
                if (!IsDirty(DashboardDirtyFields.Opacity))
                {
                    SetOpacityControl(settings.OpacityPercent);
                }

                if (!IsDirty(DashboardDirtyFields.Locked))
                {
                    lockedCheckBox.Checked = settings.Locked;
                }

                if (!IsDirty(DashboardDirtyFields.ClickThrough))
                {
                    clickThroughCheckBox.Checked = settings.ClickThrough;
                }

                if (!IsDirty(DashboardDirtyFields.History))
                {
                    persistHistoryCheckBox.Checked = settings.PersistHistory;
                }

                if (!IsDirty(DashboardDirtyFields.Gateway))
                {
                    gatewayLatencyCheckBox.Checked = settings.GatewayLatencyEnabled;
                }

                if (!IsDirty(DashboardDirtyFields.Gpu))
                {
                    SetGpuControls(settings);
                }

                if (!IsDirty(DashboardDirtyFields.Startup))
                {
                    startupCheckBox.Checked = settings.StartupEnabled;
                }

                if (!IsDirty(DashboardDirtyFields.Update))
                {
                    automaticUpdateCheckBox.Checked = settings.AutomaticUpdateEnabled;
                    updateEndpointTextBox.Text = settings.UpdateManifestUrl ?? string.Empty;
                }

                if (!IsDirty(DashboardDirtyFields.LoopbackApi))
                {
                    loopbackApiCheckBox.Checked = settings.LoopbackApiEnabled;
                }

                if (!IsDirty(DashboardDirtyFields.Telemetry))
                {
                    telemetryCheckBox.Checked = settings.TelemetryEnabled;
                    telemetryEndpointTextBox.Text = settings.TelemetryEndpoint ?? string.Empty;
                }
            }
            finally
            {
                synchronizingSettings = wasSynchronizing;
            }
        }

        internal void SetStartupStatus(string text)
        {
            startupStatusLabel.Text = string.IsNullOrWhiteSpace(text) ? "状态未知" : text;
        }

        internal void SetStartupState(bool enabled, string text)
        {
            if (!IsDirty(DashboardDirtyFields.Startup))
            {
                bool wasSynchronizing = synchronizingSettings;
                synchronizingSettings = true;
                try
                {
                    startupCheckBox.Checked = enabled;
                }
                finally
                {
                    synchronizingSettings = wasSynchronizing;
                }
            }

            SetStartupStatus(text);
        }

        internal void SetUpdateStatus(string text)
        {
            updateStatusLabel.Text = string.IsNullOrWhiteSpace(text) ? "状态未知" : text;
        }

        internal void SetApiStatus(string text)
        {
            apiStatusTextBox.Text = string.IsNullOrWhiteSpace(text) ? "已关闭" : text;
        }

        internal void SetTelemetryStatus(string text)
        {
            telemetryStatusLabel.Text = string.IsNullOrWhiteSpace(text) ? "状态未知" : text;
        }

        internal void UpdateSnapshot(HardwareSnapshot snapshot)
        {
            if (snapshot == null || IsDisposed)
            {
                return;
            }

            latestSnapshot = snapshot;

            cpuValue.Text = FormatPercent(snapshot.CpuPercent);
            memoryValue.Text = FormatPercent(snapshot.MemoryPercent);
            discreteGpuValue.Text = FormatDiscreteGpuMetrics(snapshot);
            discreteGpuName.Text = FormatGpuName(snapshot.DiscreteGpuName);
            integratedGpuValue.Text = FormatIntegratedGpuMetrics(snapshot);
            integratedGpuName.Text = FormatGpuName(snapshot.IntegratedGpuName);
            receiveValue.Text = FormatRate(snapshot.NetworkReceiveBytesPerSecond);
            sendValue.Text = FormatRate(snapshot.NetworkSendBytesPerSecond);
            linkValue.Text = FormatLinkSpeed(snapshot.NetworkLinkSpeedBitsPerSecond);
            adapterValue.Text = FormatNetworkAdapter(snapshot);
            gatewayLatencyValue.Text = FormatGatewayLatency(snapshot);
            gatewayValue.Text = string.IsNullOrWhiteSpace(snapshot.NetworkGatewayAddress)
                ? "--"
                : snapshot.NetworkGatewayAddress;
            historyChart.SetPoints(history.Snapshot());
            UpdateGpuSelectionStatus(null, EventArgs.Empty);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ConstrainWindowToWorkingArea();
        }

        internal static Rectangle ConstrainToWorkingArea(
            Rectangle bounds,
            Rectangle workingArea,
            int margin)
        {
            int safeMargin = Math.Max(0, margin);
            int availableWidth = Math.Max(1, workingArea.Width - safeMargin * 2);
            int availableHeight = Math.Max(1, workingArea.Height - safeMargin * 2);
            int width = Math.Min(Math.Max(1, bounds.Width), availableWidth);
            int height = Math.Min(Math.Max(1, bounds.Height), availableHeight);
            int minimumLeft = workingArea.Left + safeMargin;
            int minimumTop = workingArea.Top + safeMargin;
            int maximumLeft = workingArea.Right - safeMargin - width;
            int maximumTop = workingArea.Bottom - safeMargin - height;
            int left = Math.Max(minimumLeft, Math.Min(bounds.Left, maximumLeft));
            int top = Math.Max(minimumTop, Math.Min(bounds.Top, maximumTop));
            return new Rectangle(left, top, width, height);
        }

        private void ConstrainWindowToWorkingArea()
        {
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            Rectangle constrained = ConstrainToWorkingArea(Bounds, workingArea, 12);
            MinimumSize = new Size(
                Math.Min(MinimumSize.Width, constrained.Width),
                Math.Min(MinimumSize.Height, constrained.Height));
            if (Bounds != constrained)
            {
                Bounds = constrained;
            }
        }

        protected override void Dispose(bool disposing)
        {
            bool disposeResources = disposing && !resourcesDisposed;
            if (disposeResources)
            {
                resourcesDisposed = true;
                Icon = null;
                applicationIcon.Dispose();
            }

            base.Dispose(disposing);

            if (disposeResources)
            {
                metricFont.Dispose();
                uiFont.Dispose();
            }
        }

        private static Label CreateFieldLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 7, 10, 7);
            return label;
        }

        private static Label CreateStatusLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Dock = DockStyle.Fill;
            label.ForeColor = Color.FromArgb(71, 85, 105);
            label.Margin = new Padding(0, 2, 0, 8);
            return label;
        }

        private static Label CreateWrappedNote(
            string text,
            Color background,
            Color foreground)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Dock = DockStyle.Top;
            label.Height = 58;
            label.Padding = new Padding(10);
            label.BackColor = background;
            label.ForeColor = foreground;
            label.Margin = new Padding(0, 0, 0, 8);
            return label;
        }

        private Label CreateMetricCard(
            TableLayoutPanel layout,
            string title,
            int column,
            int row)
        {
            GroupBox card = new GroupBox();
            card.Text = title;
            card.Dock = DockStyle.Fill;
            card.BackColor = Color.White;
            card.ForeColor = Color.FromArgb(71, 85, 105);
            card.Padding = new Padding(12);
            card.Margin = new Padding(6);

            Label value = new Label();
            value.Text = "--";
            value.Dock = DockStyle.Fill;
            value.TextAlign = ContentAlignment.MiddleCenter;
            value.Font = metricFont;
            value.ForeColor = Color.FromArgb(15, 23, 42);
            value.AutoEllipsis = true;
            value.AccessibleName = title;
            card.Controls.Add(value);
            layout.Controls.Add(card, column, row);
            return value;
        }

        private Label CreateGpuMetricCard(
            TableLayoutPanel layout,
            string title,
            int column,
            int row,
            out Label adapterName)
        {
            GroupBox card = new GroupBox();
            card.Text = title;
            card.Dock = DockStyle.Fill;
            card.BackColor = Color.White;
            card.ForeColor = Color.FromArgb(71, 85, 105);
            card.Padding = new Padding(12, 8, 12, 6);
            card.Margin = new Padding(6);

            TableLayoutPanel content = new TableLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.ColumnCount = 1;
            content.RowCount = 2;
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 65.0f));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 35.0f));
            content.Margin = Padding.Empty;
            content.Padding = Padding.Empty;

            Label value = new Label();
            value.Text = "--";
            value.Dock = DockStyle.Fill;
            value.TextAlign = ContentAlignment.MiddleCenter;
            value.Font = metricFont;
            value.ForeColor = Color.FromArgb(15, 23, 42);
            value.AutoEllipsis = false;
            value.UseMnemonic = false;
            value.MinimumSize = new Size(0, metricFont.Height);
            value.Margin = Padding.Empty;
            value.AccessibleName = title;
            value.AccessibleDescription = title + "的实时利用率、显存和温度等核心数值";

            adapterName = new Label();
            adapterName.Text = string.Empty;
            adapterName.Dock = DockStyle.Fill;
            adapterName.TextAlign = ContentAlignment.MiddleCenter;
            adapterName.Font = uiFont;
            adapterName.ForeColor = Color.FromArgb(71, 85, 105);
            adapterName.AutoEllipsis = true;
            adapterName.UseMnemonic = false;
            adapterName.Margin = Padding.Empty;
            adapterName.AccessibleName = title + "型号";
            adapterName.AccessibleDescription = title + "的设备名称，空间不足时可缩略显示";

            content.Controls.Add(value, 0, 0);
            content.Controls.Add(adapterName, 0, 1);
            card.Controls.Add(content);
            layout.Controls.Add(card, column, row);
            return value;
        }

        private void WireSettingsDirtyTracking()
        {
            opacityComboBox.SelectedIndexChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Opacity);
            };
            lockedCheckBox.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Locked);
            };
            clickThroughCheckBox.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.ClickThrough);
            };
            persistHistoryCheckBox.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.History);
            };
            gatewayLatencyCheckBox.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Gateway);
            };
            automaticGpuRadioButton.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Gpu);
            };
            manualGpuRadioButton.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Gpu);
            };
            gpuAdapterComboBox.SelectedIndexChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Gpu);
            };
            startupCheckBox.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Startup);
            };
            automaticUpdateCheckBox.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Update);
            };
            updateEndpointTextBox.TextChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Update);
            };
            loopbackApiCheckBox.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.LoopbackApi);
            };
            telemetryCheckBox.CheckedChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Telemetry);
            };
            telemetryEndpointTextBox.TextChanged += delegate
            {
                MarkDirty(DashboardDirtyFields.Telemetry);
            };
        }

        private void MarkDirty(DashboardDirtyFields field)
        {
            if (!synchronizingSettings)
            {
                dirtyFields |= field;
            }
        }

        private bool IsDirty(DashboardDirtyFields field)
        {
            return (dirtyFields & field) != 0;
        }

        private void SetDisplayControls(AppSettings settings)
        {
            SetOpacityControl(settings.OpacityPercent);
            lockedCheckBox.Checked = settings.Locked;
            clickThroughCheckBox.Checked = settings.ClickThrough;
        }

        private void SetOpacityControl(int opacityPercent)
        {
            int selectedIndex = Array.IndexOf(
                AppSettings.SupportedOpacityPercentages,
                opacityPercent);
            opacityComboBox.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        }

        private void SetGpuControls(AppSettings settings)
        {
            automaticGpuRadioButton.Checked =
                settings.GpuSelectionMode != GpuSelectionMode.Manual;
            manualGpuRadioButton.Checked =
                settings.GpuSelectionMode == GpuSelectionMode.Manual;
            SelectGpuAdapter(
                settings.SelectedGpuAdapterId,
                settings.SelectedGpuAdapterName);
            UpdateGpuSelectionControls(null, EventArgs.Empty);
        }

        private void RefreshGpuAdapters(object sender, EventArgs e)
        {
            GpuChoiceItem selected = gpuAdapterComboBox.SelectedItem as GpuChoiceItem;
            RefreshGpuAdapterChoices(
                selected == null ? string.Empty : selected.AdapterId,
                selected == null ? string.Empty : selected.Name);
            UpdateGpuSelectionControls(null, EventArgs.Empty);
        }

        private void RefreshGpuAdapterChoices(
            string selectedAdapterId,
            string selectedAdapterName)
        {
            gpuAdapterComboBox.BeginUpdate();
            try
            {
                gpuAdapterComboBox.Items.Clear();
                foreach (GpuAdapterChoice choice in GpuRoleSampler.GetAdapterChoices())
                {
                    gpuAdapterComboBox.Items.Add(new GpuChoiceItem(choice, true));
                }

                SelectGpuAdapter(selectedAdapterId, selectedAdapterName);
            }
            finally
            {
                gpuAdapterComboBox.EndUpdate();
            }
        }

        private void SelectGpuAdapter(string adapterId, string adapterName)
        {
            if (!string.IsNullOrWhiteSpace(adapterId))
            {
                for (int index = 0; index < gpuAdapterComboBox.Items.Count; index++)
                {
                    GpuChoiceItem item = gpuAdapterComboBox.Items[index] as GpuChoiceItem;
                    if (item != null && string.Equals(
                            item.AdapterId,
                            adapterId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        gpuAdapterComboBox.SelectedIndex = index;
                        return;
                    }
                }

                int nameMatchIndex = -1;
                int nameMatchCount = 0;
                if (!string.IsNullOrWhiteSpace(adapterName))
                {
                    for (int index = 0; index < gpuAdapterComboBox.Items.Count; index++)
                    {
                        GpuChoiceItem item = gpuAdapterComboBox.Items[index] as GpuChoiceItem;
                        if (item != null && item.IsAvailable && string.Equals(
                                item.Name,
                                adapterName.Trim(),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            nameMatchIndex = index;
                            nameMatchCount++;
                        }
                    }
                }

                if (nameMatchCount == 1)
                {
                    gpuAdapterComboBox.SelectedIndex = nameMatchIndex;
                    return;
                }

                gpuAdapterComboBox.Items.Add(
                    GpuChoiceItem.CreateUnavailable(adapterId, adapterName));
                gpuAdapterComboBox.SelectedIndex = gpuAdapterComboBox.Items.Count - 1;
                return;
            }

            if (gpuAdapterComboBox.Items.Count > 0)
            {
                gpuAdapterComboBox.SelectedIndex = 0;
            }
        }

        private void UpdateGpuSelectionControls(object sender, EventArgs e)
        {
            gpuAdapterComboBox.Enabled = manualGpuRadioButton.Checked;
            UpdateGpuSelectionStatus(null, EventArgs.Empty);
        }

        private void UpdateGpuSelectionStatus(object sender, EventArgs e)
        {
            if (automaticGpuRadioButton.Checked)
            {
                gpuSelectionStatusLabel.ForeColor = Color.FromArgb(30, 64, 175);
                gpuSelectionStatusLabel.Text = latestSnapshot != null &&
                    !string.IsNullOrWhiteSpace(latestSnapshot.EffectiveGpuAdapterName)
                    ? "当前自动使用：" + latestSnapshot.EffectiveGpuAdapterName
                    : "自动模式：正在检测最合适的 GPU";
                return;
            }

            GpuChoiceItem selected = gpuAdapterComboBox.SelectedItem as GpuChoiceItem;
            if (selected == null)
            {
                gpuSelectionStatusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                gpuSelectionStatusLabel.Text = "未检测到可手动选择的 GPU，请使用自动模式。";
                return;
            }

            if (!selected.IsAvailable ||
                (latestSnapshot != null &&
                 latestSnapshot.GpuSelectionStatus ==
                    GpuSelectionStatus.ManualUnavailableFallback &&
                 string.Equals(
                    latestSnapshot.RequestedGpuAdapterId,
                    selected.AdapterId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                gpuSelectionStatusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                gpuSelectionStatusLabel.Text =
                    "所选设备当前不可用；应用后将临时回退自动选择，设备恢复后自动重试。";
                return;
            }

            gpuSelectionStatusLabel.ForeColor = Color.FromArgb(22, 101, 52);
            gpuSelectionStatusLabel.Text = "手动选择：" + selected.Name;
        }

        private void ClearHistory(object sender, EventArgs e)
        {
            DialogResult confirmation = MessageBox.Show(
                this,
                "将清除当前会话中的历史曲线，并删除 TinyHwBar 自己的本地历史文件和备份。此操作无法撤销。\r\n\r\n是否继续？",
                "确认清除历史",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            history.Clear();
            historyChart.SetPoints(history.Snapshot());

            EventHandler handler = HistoryCleared;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void ApplySettings(object sender, EventArgs e)
        {
            int selectedIndex = opacityComboBox.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= AppSettings.SupportedOpacityPercentages.Length)
            {
                return;
            }

            GpuSelectionMode gpuSelectionMode = manualGpuRadioButton.Checked
                ? GpuSelectionMode.Manual
                : GpuSelectionMode.Automatic;
            GpuChoiceItem selectedGpu = gpuAdapterComboBox.SelectedItem as GpuChoiceItem;
            if (gpuSelectionMode == GpuSelectionMode.Manual && selectedGpu == null)
            {
                gpuSelectionStatusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                gpuSelectionStatusLabel.Text =
                    "没有可保存的手动设备；请选择自动模式或刷新设备列表。";
                gpuAdapterComboBox.Focus();
                return;
            }

            EventHandler<DashboardSettingsChangedEventArgs> handler = SettingsApplied;
            if (handler != null)
            {
                handler(
                    this,
                    new DashboardSettingsChangedEventArgs(
                        lockedCheckBox.Checked,
                        clickThroughCheckBox.Checked,
                        AppSettings.SupportedOpacityPercentages[selectedIndex],
                        persistHistoryCheckBox.Checked,
                        gatewayLatencyCheckBox.Checked,
                        gpuSelectionMode,
                        selectedGpu == null ? string.Empty : selectedGpu.AdapterId,
                        selectedGpu == null ? string.Empty : selectedGpu.Name,
                        startupCheckBox.Checked,
                        automaticUpdateCheckBox.Checked,
                        updateEndpointTextBox.Text,
                        loopbackApiCheckBox.Checked,
                        telemetryCheckBox.Checked,
                        telemetryEndpointTextBox.Text));
            }
        }

        private void RequestUpdateCheck(object sender, EventArgs e)
        {
            EventHandler<DashboardEndpointActionEventArgs> handler = UpdateCheckRequested;
            if (handler != null)
            {
                handler(
                    this,
                    new DashboardEndpointActionEventArgs(
                        true,
                        updateEndpointTextBox.Text));
            }
        }

        private void RequestTelemetryPreview(object sender, EventArgs e)
        {
            EventHandler<DashboardEndpointActionEventArgs> handler =
                TelemetryPreviewRequested;
            if (handler != null)
            {
                handler(
                    this,
                    new DashboardEndpointActionEventArgs(
                        telemetryCheckBox.Checked,
                        telemetryEndpointTextBox.Text));
            }
        }

        private void RequestDiagnosticPreview(object sender, EventArgs e)
        {
            EventHandler<DashboardEndpointActionEventArgs> handler =
                DiagnosticPreviewRequested;
            if (handler != null)
            {
                handler(
                    this,
                    new DashboardEndpointActionEventArgs(
                        telemetryCheckBox.Checked,
                        telemetryEndpointTextBox.Text));
            }
        }

        private void CloseWindow(object sender, EventArgs e)
        {
            Close();
        }

        private static string FormatDiscreteGpuMetrics(HardwareSnapshot snapshot)
        {
            if (snapshot.GpuMode == GpuDisplayMode.Eco)
            {
                return "ECO";
            }

            if (!snapshot.DiscreteGpuDetected ||
                snapshot.GpuMode != GpuDisplayMode.Available)
            {
                return FormatGpuUnavailable(
                    snapshot.DiscreteAdapterStatus,
                    "独立显卡");
            }

            string gpu = FormatPercent(snapshot.GpuPercent);
            string memory = FormatPercent(snapshot.VideoMemoryPercent);
            string temperature = snapshot.TemperatureCelsius.HasValue
                ? snapshot.TemperatureCelsius.Value.ToString(CultureInfo.CurrentCulture) + "°C"
                : "--°C";
            return gpu + " · 显存 " + memory + " · " + temperature;
        }

        private static string FormatIntegratedGpuMetrics(HardwareSnapshot snapshot)
        {
            if (!snapshot.IntegratedGpuDetected)
            {
                return FormatGpuUnavailable(
                    snapshot.IntegratedAdapterStatus,
                    "核显");
            }

            string utilization = FormatPercent(snapshot.IntegratedGpuPercent);
            if (snapshot.IntegratedSharedMemoryBytes.HasValue)
            {
                return utilization + " · 共享 " +
                    FormatBytes(snapshot.IntegratedSharedMemoryBytes.Value);
            }

            if (snapshot.IntegratedDedicatedMemoryBytes.HasValue)
            {
                return utilization + " · 显存 " +
                    FormatBytes(snapshot.IntegratedDedicatedMemoryBytes.Value);
            }

            string utilizationState = snapshot.IntegratedUtilizationStatus ==
                IntelGpuDataStatus.FirstSamplePending
                ? "首个样本…"
                : "性能不可用";
            if (snapshot.IntegratedSharedMemoryLimitBytes.HasValue)
            {
                return utilizationState + " · 共享上限 " +
                    FormatBytes(snapshot.IntegratedSharedMemoryLimitBytes.Value);
            }

            return utilizationState;
        }

        private static string FormatGpuName(string adapterName)
        {
            return string.IsNullOrWhiteSpace(adapterName)
                ? string.Empty
                : adapterName.Trim();
        }

        private static string FormatGpuUnavailable(
            IntelGpuDataStatus status,
            string roleName)
        {
            switch (status)
            {
                case IntelGpuDataStatus.AmbiguousAdapter:
                    return "检测到多个" + roleName + "，未自动选择";
                case IntelGpuDataStatus.AccessDenied:
                    return roleName + "检测权限不足";
                case IntelGpuDataStatus.ProbeFailed:
                case IntelGpuDataStatus.SampleFailed:
                    return roleName + "检测失败";
                case IntelGpuDataStatus.Unsupported:
                    return "系统未能可靠识别" + roleName;
                default:
                    return "未检测到" + roleName;
            }
        }

        private static string FormatPercent(int? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.CurrentCulture) + "%"
                : "--";
        }

        private static string FormatRate(long? bytesPerSecond)
        {
            return bytesPerSecond.HasValue
                ? FormatBytes(bytesPerSecond.Value) + "/s"
                : "--";
        }

        private static string FormatNetworkAdapter(HardwareSnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.NetworkAdapterName))
            {
                return snapshot.NetworkAdapterName;
            }

            switch (snapshot.NetworkSelectionStatus)
            {
                case NetworkSelectionStatus.NoRoute:
                    return "无可用路由";
                case NetworkSelectionStatus.SplitRoute:
                    return "IPv4 / IPv6 使用不同适配器";
                case NetworkSelectionStatus.RouteLookupFailed:
                    return "无法确认当前路由";
                case NetworkSelectionStatus.RouteInterfaceMissing:
                    return "路由接口暂不可用";
                case NetworkSelectionStatus.CounterUnavailable:
                    return "网络计数器不可用";
                case NetworkSelectionStatus.Disposed:
                    return "网络采样已停止";
                case NetworkSelectionStatus.Available:
                    return "适配器名称不可用";
                default:
                    return "网络状态不可用";
            }
        }

        private static string FormatLinkSpeed(long? bitsPerSecond)
        {
            if (!bitsPerSecond.HasValue || bitsPerSecond.Value <= 0)
            {
                return "--";
            }

            double megabits = bitsPerSecond.Value / 1000000.0;
            return megabits >= 1000.0
                ? (megabits / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " Gbps"
                : megabits.ToString("0.#", CultureInfo.CurrentCulture) + " Mbps";
        }

        private static string FormatGatewayLatency(HardwareSnapshot snapshot)
        {
            if (snapshot.GatewayLatencyMilliseconds.HasValue)
            {
                return snapshot.GatewayLatencyMilliseconds.Value.ToString(
                    CultureInfo.CurrentCulture) + " ms";
            }

            switch (snapshot.GatewayLatencyStatus)
            {
                case GatewayLatencyStatus.Disabled:
                    return "已关闭";
                case GatewayLatencyStatus.Waiting:
                case GatewayLatencyStatus.Probing:
                    return "测量中…";
                case GatewayLatencyStatus.GatewayMissing:
                    return "接口未报告网关";
                case GatewayLatencyStatus.GatewayRejected:
                    return "非本地网关，已拒绝";
                case GatewayLatencyStatus.TimedOut:
                    return "超时";
                case GatewayLatencyStatus.Unreachable:
                    return "不可达";
                case GatewayLatencyStatus.AccessDenied:
                    return "权限不足";
                case GatewayLatencyStatus.NetworkUnavailable:
                    return "网络不可用";
                default:
                    return "--";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0)
            {
                return "--";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024.0 && unitIndex < units.Length - 1)
            {
                value /= 1024.0;
                unitIndex++;
            }

            string format = value >= 100.0 ? "0" : value >= 10.0 ? "0.0" : "0.00";
            return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unitIndex];
        }

        private sealed class GpuChoiceItem
        {
            private GpuChoiceItem(
                string adapterId,
                string name,
                GpuAdapterRole role,
                bool isAvailable)
            {
                AdapterId = adapterId ?? string.Empty;
                Name = string.IsNullOrWhiteSpace(name) ? "未命名 GPU" : name.Trim();
                Role = role;
                IsAvailable = isAvailable;
            }

            internal GpuChoiceItem(GpuAdapterChoice choice, bool isAvailable)
                : this(
                    choice == null ? string.Empty : choice.AdapterId,
                    choice == null ? string.Empty : choice.Name,
                    choice == null ? GpuAdapterRole.Discrete : choice.Role,
                    isAvailable)
            {
            }

            internal string AdapterId { get; private set; }
            internal string Name { get; private set; }
            internal GpuAdapterRole Role { get; private set; }
            internal bool IsAvailable { get; private set; }

            internal static GpuChoiceItem CreateUnavailable(
                string adapterId,
                string adapterName)
            {
                return new GpuChoiceItem(
                    adapterId,
                    string.IsNullOrWhiteSpace(adapterName)
                        ? "之前选择的 GPU"
                        : adapterName,
                    GpuAdapterRole.Discrete,
                    false);
            }

            public override string ToString()
            {
                if (!IsAvailable)
                {
                    return Name + "（当前不可用）";
                }

                return Name + (Role == GpuAdapterRole.Discrete
                    ? "（独立显卡）"
                    : "（核显）");
            }
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
    }
}
