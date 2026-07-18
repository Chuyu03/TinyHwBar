using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace TinyHwBar
{
    internal sealed class HistoryChartControl : Control
    {
        internal const string DiscreteGpuLegendLabel = "独立显卡";
        internal const string IntegratedGpuLegendLabel = "核显";

        private const float OuterMargin = 12.0f;
        private const float AxisLabelWidth = 34.0f;
        private const float BottomLabelHeight = 24.0f;
        private const float MinimumPlotDimension = 40.0f;
        private const float MinimumConnectionGapSeconds = 10.0f;
        private const float MaximumConnectionGapSeconds = 60.0f;

        private static readonly HistoryPoint[] EmptyPoints = new HistoryPoint[0];

        private readonly Pen borderPen;
        private readonly Pen gridPen;
        private readonly Pen cpuPen;
        private readonly Pen memoryPen;
        private readonly Pen nvidiaPen;
        private readonly Pen intelPen;
        private readonly SolidBrush plotBackgroundBrush;
        private readonly SolidBrush primaryTextBrush;
        private readonly SolidBrush secondaryTextBrush;
        private readonly Font legendFont;
        private readonly Font captionFont;
        private readonly Font emptyStateFont;

        private HistoryPoint[] points = EmptyPoints;
        private bool resourcesDisposed;

        internal HistoryChartControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            SetStyle(ControlStyles.Selectable, false);

            BackColor = Color.FromArgb(248, 250, 252);
            ForeColor = Color.FromArgb(51, 65, 85);
            MinimumSize = new Size(360, 220);
            TabStop = false;
            AccessibleName = "TinyHwBar history chart";
            AccessibleDescription = "CPU, memory, discrete GPU, and integrated GPU usage history.";
            AccessibleRole = AccessibleRole.Graphic;

            borderPen = new Pen(Color.FromArgb(203, 213, 225), 1.0f);
            gridPen = new Pen(Color.FromArgb(226, 232, 240), 1.0f);
            gridPen.DashStyle = DashStyle.Dot;

            cpuPen = CreateSeriesPen(Color.FromArgb(73, 126, 214));
            memoryPen = CreateSeriesPen(Color.FromArgb(66, 160, 127));
            nvidiaPen = CreateSeriesPen(Color.FromArgb(128, 99, 191));
            intelPen = CreateSeriesPen(Color.FromArgb(202, 118, 35));

            plotBackgroundBrush = new SolidBrush(Color.White);
            primaryTextBrush = new SolidBrush(ForeColor);
            secondaryTextBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
            legendFont = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
            captionFont = new Font("Segoe UI", 8.0f, FontStyle.Regular, GraphicsUnit.Point);
            emptyStateFont = new Font("Segoe UI", 10.0f, FontStyle.Regular, GraphicsUnit.Point);
        }

        internal void SetPoints(HistoryPoint[] historyPoints)
        {
            if (historyPoints == null || historyPoints.Length == 0)
            {
                points = EmptyPoints;
            }
            else
            {
                HistoryPoint[] copy = new HistoryPoint[historyPoints.Length];
                Array.Copy(historyPoints, copy, historyPoints.Length);
                Array.Sort(copy, CompareHistoryPoints);
                points = copy;
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            float dpiScale = GetDpiScale(graphics);
            UpdatePenWidths(dpiScale);
            float outerMargin = OuterMargin * dpiScale;
            float axisLabelWidth = AxisLabelWidth * dpiScale;
            float bottomLabelHeight = BottomLabelHeight * dpiScale;
            float legendBottom = DrawLegend(graphics, dpiScale, outerMargin);
            RectangleF plotBounds = new RectangleF(
                outerMargin + axisLabelWidth,
                legendBottom,
                Math.Max(0.0f, ClientSize.Width - (outerMargin * 2.0f) - axisLabelWidth),
                Math.Max(
                    0.0f,
                    ClientSize.Height - legendBottom - bottomLabelHeight - outerMargin));

            if (plotBounds.Width < MinimumPlotDimension * dpiScale ||
                plotBounds.Height < MinimumPlotDimension * dpiScale)
            {
                DrawCenteredMessage(graphics, "窗口空间不足", ClientRectangle);
                return;
            }

            DrawPlotArea(
                graphics,
                plotBounds,
                dpiScale,
                outerMargin,
                axisLabelWidth);

            HistoryPoint[] currentPoints = points;
            if (currentPoints.Length == 0)
            {
                DrawCenteredMessage(
                    graphics,
                    "暂无历史数据\n运行后将显示最近约 30 分钟趋势",
                    Rectangle.Round(plotBounds));
                return;
            }

            DateTime startUtc;
            DateTime endUtc;
            int pointCount;
            if (!TryGetTimeRange(currentPoints, out startUtc, out endUtc, out pointCount))
            {
                DrawCenteredMessage(graphics, "暂无可绘制指标", Rectangle.Round(plotBounds));
                return;
            }

            TimeSpan maximumConnectedGap = CalculateConnectionGapThreshold(currentPoints);
            DrawSeries(
                graphics,
                plotBounds,
                currentPoints,
                startUtc,
                endUtc,
                maximumConnectedGap,
                SelectCpuPercent,
                cpuPen,
                dpiScale);
            DrawSeries(
                graphics,
                plotBounds,
                currentPoints,
                startUtc,
                endUtc,
                maximumConnectedGap,
                SelectMemoryPercent,
                memoryPen,
                dpiScale);
            DrawSeries(
                graphics,
                plotBounds,
                currentPoints,
                startUtc,
                endUtc,
                maximumConnectedGap,
                SelectDiscreteGpuPercent,
                nvidiaPen,
                dpiScale);
            DrawSeries(
                graphics,
                plotBounds,
                currentPoints,
                startUtc,
                endUtc,
                maximumConnectedGap,
                SelectIntegratedGpuPercent,
                intelPen,
                dpiScale);
            DrawTimeRange(
                graphics,
                plotBounds,
                startUtc,
                endUtc,
                pointCount,
                bottomLabelHeight,
                dpiScale);

            if (!HasPlottableData(currentPoints))
            {
                DrawCenteredMessage(graphics, "暂无可绘制指标", Rectangle.Round(plotBounds));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true;
                borderPen.Dispose();
                gridPen.Dispose();
                cpuPen.Dispose();
                memoryPen.Dispose();
                nvidiaPen.Dispose();
                intelPen.Dispose();
                plotBackgroundBrush.Dispose();
                primaryTextBrush.Dispose();
                secondaryTextBrush.Dispose();
                legendFont.Dispose();
                captionFont.Dispose();
                emptyStateFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private static Pen CreateSeriesPen(Color color)
        {
            Pen pen = new Pen(color, 1.8f);
            pen.LineJoin = LineJoin.Round;
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            return pen;
        }

        private float DrawLegend(
            Graphics graphics,
            float dpiScale,
            float outerMargin)
        {
            float x = outerMargin;
            float y = outerMargin;
            float rowHeight = Math.Max(
                18.0f * dpiScale,
                legendFont.GetHeight(graphics) + (4.0f * dpiScale));
            float rightEdge = Math.Max(outerMargin, ClientSize.Width - outerMargin);

            DrawLegendItem(
                graphics,
                "CPU",
                cpuPen,
                ref x,
                ref y,
                rowHeight,
                rightEdge,
                outerMargin,
                dpiScale);
            DrawLegendItem(
                graphics,
                "RAM",
                memoryPen,
                ref x,
                ref y,
                rowHeight,
                rightEdge,
                outerMargin,
                dpiScale);
            DrawLegendItem(
                graphics,
                DiscreteGpuLegendLabel,
                nvidiaPen,
                ref x,
                ref y,
                rowHeight,
                rightEdge,
                outerMargin,
                dpiScale);
            DrawLegendItem(
                graphics,
                IntegratedGpuLegendLabel,
                intelPen,
                ref x,
                ref y,
                rowHeight,
                rightEdge,
                outerMargin,
                dpiScale);

            return y + rowHeight + (4.0f * dpiScale);
        }

        private void DrawLegendItem(
            Graphics graphics,
            string label,
            Pen pen,
            ref float x,
            ref float y,
            float rowHeight,
            float rightEdge,
            float outerMargin,
            float dpiScale)
        {
            SizeF labelSize = graphics.MeasureString(label, legendFont);
            float itemWidth = (24.0f * dpiScale) +
                labelSize.Width +
                (16.0f * dpiScale);

            if (x > outerMargin && x + itemWidth > rightEdge)
            {
                x = outerMargin;
                y += rowHeight;
            }

            float centerY = y + (rowHeight / 2.0f);
            graphics.DrawLine(pen, x, centerY, x + (16.0f * dpiScale), centerY);
            graphics.DrawString(
                label,
                legendFont,
                primaryTextBrush,
                x + (22.0f * dpiScale),
                y + ((rowHeight - labelSize.Height) / 2.0f));
            x += itemWidth;
        }

        private void DrawPlotArea(
            Graphics graphics,
            RectangleF plotBounds,
            float dpiScale,
            float outerMargin,
            float axisLabelWidth)
        {
            graphics.FillRectangle(plotBackgroundBrush, plotBounds);

            using (StringFormat percentageFormat = new StringFormat())
            {
                percentageFormat.Alignment = StringAlignment.Far;
                percentageFormat.LineAlignment = StringAlignment.Center;

                for (int percentage = 0; percentage <= 100; percentage += 25)
                {
                    float y = ValueToY(percentage, plotBounds);
                    graphics.DrawLine(gridPen, plotBounds.Left, y, plotBounds.Right, y);
                    graphics.DrawString(
                        percentage.ToString(CultureInfo.InvariantCulture) + "%",
                        captionFont,
                        secondaryTextBrush,
                        new RectangleF(
                            outerMargin,
                            y - (9.0f * dpiScale),
                            axisLabelWidth - (5.0f * dpiScale),
                            18.0f * dpiScale),
                        percentageFormat);
                }
            }

            for (int division = 1; division < 4; division++)
            {
                float x = plotBounds.Left + (plotBounds.Width * division / 4.0f);
                graphics.DrawLine(gridPen, x, plotBounds.Top, x, plotBounds.Bottom);
            }

            graphics.DrawRectangle(
                borderPen,
                plotBounds.X,
                plotBounds.Y,
                plotBounds.Width,
                plotBounds.Height);
        }

        private static void DrawSeries(
            Graphics graphics,
            RectangleF plotBounds,
            HistoryPoint[] historyPoints,
            DateTime startUtc,
            DateTime endUtc,
            TimeSpan maximumConnectedGap,
            Func<HistoryPoint, int?> selector,
            Pen pen,
            float dpiScale)
        {
            PointF previousPoint = PointF.Empty;
            HistoryPoint previousHistoryPoint = null;
            int segmentLength = 0;

            for (int index = 0; index < historyPoints.Length; index++)
            {
                HistoryPoint historyPoint = historyPoints[index];
                int? value = historyPoint == null ? null : selector(historyPoint);
                if (!value.HasValue)
                {
                    DrawIsolatedPoint(
                        graphics,
                        pen,
                        previousPoint,
                        segmentLength,
                        dpiScale);
                    segmentLength = 0;
                    previousHistoryPoint = null;
                    continue;
                }

                int clampedValue = Math.Max(0, Math.Min(100, value.Value));
                PointF currentPoint = new PointF(
                    MapTimestampToX(
                        historyPoint.TimestampUtc,
                        startUtc,
                        endUtc,
                        plotBounds),
                    ValueToY(clampedValue, plotBounds));

                if (segmentLength > 0 &&
                    ArePointsConnected(
                        previousHistoryPoint,
                        historyPoint,
                        maximumConnectedGap))
                {
                    graphics.DrawLine(pen, previousPoint, currentPoint);
                }
                else if (segmentLength > 0)
                {
                    DrawIsolatedPoint(
                        graphics,
                        pen,
                        previousPoint,
                        segmentLength,
                        dpiScale);
                    segmentLength = 0;
                }

                previousPoint = currentPoint;
                previousHistoryPoint = historyPoint;
                segmentLength++;
            }

            DrawIsolatedPoint(
                graphics,
                pen,
                previousPoint,
                segmentLength,
                dpiScale);
        }

        private static void DrawIsolatedPoint(
            Graphics graphics,
            Pen pen,
            PointF point,
            int segmentLength,
            float dpiScale)
        {
            if (segmentLength == 1)
            {
                float radius = 1.5f * dpiScale;
                float diameter = radius * 2.0f;
                graphics.DrawEllipse(
                    pen,
                    point.X - radius,
                    point.Y - radius,
                    diameter,
                    diameter);
            }
        }

        private void DrawTimeRange(
            Graphics graphics,
            RectangleF plotBounds,
            DateTime startUtc,
            DateTime endUtc,
            int pointCount,
            float bottomLabelHeight,
            float dpiScale)
        {
            DateTime localStart = startUtc.ToLocalTime();
            DateTime localEnd = endUtc.ToLocalTime();
            bool sameDay = localStart.Date == localEnd.Date;
            string startText = localStart.ToString(
                sameDay ? "HH:mm:ss" : "MM-dd HH:mm",
                CultureInfo.CurrentCulture);
            string endText = localEnd.ToString(
                sameDay ? "HH:mm:ss" : "MM-dd HH:mm",
                CultureInfo.CurrentCulture);
            string rangeText = FormatDuration(localEnd - localStart) + " · " +
                pointCount.ToString(CultureInfo.CurrentCulture) + " 点";
            RectangleF labelBounds = new RectangleF(
                plotBounds.Left,
                plotBounds.Bottom + (4.0f * dpiScale),
                plotBounds.Width,
                bottomLabelHeight - (4.0f * dpiScale));

            using (StringFormat leftFormat = new StringFormat())
            using (StringFormat centerFormat = new StringFormat())
            using (StringFormat rightFormat = new StringFormat())
            {
                leftFormat.Alignment = StringAlignment.Near;
                centerFormat.Alignment = StringAlignment.Center;
                rightFormat.Alignment = StringAlignment.Far;

                graphics.DrawString(
                    startText,
                    captionFont,
                    secondaryTextBrush,
                    labelBounds,
                    leftFormat);
                graphics.DrawString(
                    rangeText,
                    captionFont,
                    secondaryTextBrush,
                    labelBounds,
                    centerFormat);
                graphics.DrawString(
                    endText,
                    captionFont,
                    secondaryTextBrush,
                    labelBounds,
                    rightFormat);
            }
        }

        private void DrawCenteredMessage(Graphics graphics, string message, Rectangle bounds)
        {
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                graphics.DrawString(
                    message,
                    emptyStateFont,
                    secondaryTextBrush,
                    bounds,
                    format);
            }
        }

        private static float ValueToY(int percentage, RectangleF plotBounds)
        {
            return plotBounds.Bottom - (plotBounds.Height * percentage / 100.0f);
        }

        private static bool HasPlottableData(HistoryPoint[] historyPoints)
        {
            foreach (HistoryPoint point in historyPoints)
            {
                if (point != null &&
                    (point.CpuPercent.HasValue ||
                     point.MemoryPercent.HasValue ||
                     point.NvidiaGpuPercent.HasValue ||
                     point.IntelGpuPercent.HasValue))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CompareHistoryPoints(HistoryPoint left, HistoryPoint right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            return left.TimestampUtc.CompareTo(right.TimestampUtc);
        }

        private static bool TryGetTimeRange(
            HistoryPoint[] historyPoints,
            out DateTime startUtc,
            out DateTime endUtc,
            out int pointCount)
        {
            startUtc = DateTime.MaxValue;
            endUtc = DateTime.MinValue;
            pointCount = 0;

            foreach (HistoryPoint point in historyPoints)
            {
                if (point == null)
                {
                    continue;
                }

                if (point.TimestampUtc < startUtc)
                {
                    startUtc = point.TimestampUtc;
                }

                if (point.TimestampUtc > endUtc)
                {
                    endUtc = point.TimestampUtc;
                }

                pointCount++;
            }

            return pointCount > 0;
        }

        internal static float MapTimestampToX(
            DateTime timestampUtc,
            DateTime startUtc,
            DateTime endUtc,
            RectangleF plotBounds)
        {
            long rangeTicks = endUtc.Ticks - startUtc.Ticks;
            if (rangeTicks <= 0)
            {
                return plotBounds.Left + (plotBounds.Width / 2.0f);
            }

            double fraction = (double)(timestampUtc.Ticks - startUtc.Ticks) / rangeTicks;
            fraction = Math.Max(0.0, Math.Min(1.0, fraction));
            return plotBounds.Left + (float)(plotBounds.Width * fraction);
        }

        internal static TimeSpan CalculateConnectionGapThreshold(
            HistoryPoint[] historyPoints)
        {
            List<long> positiveGapTicks = new List<long>();
            HistoryPoint previous = null;
            foreach (HistoryPoint point in historyPoints)
            {
                if (point == null)
                {
                    continue;
                }

                if (previous != null)
                {
                    long gapTicks = point.TimestampUtc.Ticks - previous.TimestampUtc.Ticks;
                    if (gapTicks > 0)
                    {
                        positiveGapTicks.Add(gapTicks);
                    }
                }

                previous = point;
            }

            long minimumTicks = TimeSpan.FromSeconds(MinimumConnectionGapSeconds).Ticks;
            long maximumTicks = TimeSpan.FromSeconds(MaximumConnectionGapSeconds).Ticks;
            if (positiveGapTicks.Count == 0)
            {
                return TimeSpan.FromTicks(minimumTicks);
            }

            positiveGapTicks.Sort();
            long medianTicks = positiveGapTicks[positiveGapTicks.Count / 2];
            long scaledTicks = medianTicks > long.MaxValue / 4L
                ? long.MaxValue
                : medianTicks * 4L;
            long thresholdTicks = Math.Max(minimumTicks, Math.Min(maximumTicks, scaledTicks));
            return TimeSpan.FromTicks(thresholdTicks);
        }

        internal static bool ArePointsConnected(
            HistoryPoint previous,
            HistoryPoint current,
            TimeSpan maximumConnectedGap)
        {
            if (previous == null || current == null)
            {
                return false;
            }

            TimeSpan gap = current.TimestampUtc - previous.TimestampUtc;
            return gap > TimeSpan.Zero && gap <= maximumConnectedGap;
        }

        private void UpdatePenWidths(float dpiScale)
        {
            borderPen.Width = 1.0f * dpiScale;
            gridPen.Width = 1.0f * dpiScale;
            cpuPen.Width = 1.8f * dpiScale;
            memoryPen.Width = 1.8f * dpiScale;
            nvidiaPen.Width = 1.8f * dpiScale;
            intelPen.Width = 1.8f * dpiScale;
        }

        private static float GetDpiScale(Graphics graphics)
        {
            float dpiX = graphics == null ? 96.0f : graphics.DpiX;
            if (float.IsNaN(dpiX) || float.IsInfinity(dpiX) || dpiX <= 0.0f)
            {
                dpiX = 96.0f;
            }

            return Math.Max(1.0f, dpiX / 96.0f);
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            if (duration.TotalHours >= 1.0)
            {
                return duration.TotalHours.ToString("0.#", CultureInfo.CurrentCulture) + " 小时";
            }

            if (duration.TotalMinutes >= 1.0)
            {
                return Math.Max(1, (int)Math.Round(duration.TotalMinutes))
                    .ToString(CultureInfo.CurrentCulture) + " 分钟";
            }

            return Math.Max(0, (int)Math.Round(duration.TotalSeconds))
                .ToString(CultureInfo.CurrentCulture) + " 秒";
        }

        private static int? SelectCpuPercent(HistoryPoint point)
        {
            return point.CpuPercent;
        }

        private static int? SelectMemoryPercent(HistoryPoint point)
        {
            return point.MemoryPercent;
        }

        private static int? SelectDiscreteGpuPercent(HistoryPoint point)
        {
            return point.DiscreteGpuPercent;
        }

        private static int? SelectIntegratedGpuPercent(HistoryPoint point)
        {
            return point.IntegratedGpuPercent;
        }
    }
}
