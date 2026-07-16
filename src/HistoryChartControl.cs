using System;
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

            float legendBottom = DrawLegend(graphics);
            RectangleF plotBounds = new RectangleF(
                OuterMargin + AxisLabelWidth,
                legendBottom,
                Math.Max(0.0f, ClientSize.Width - (OuterMargin * 2.0f) - AxisLabelWidth),
                Math.Max(
                    0.0f,
                    ClientSize.Height - legendBottom - BottomLabelHeight - OuterMargin));

            if (plotBounds.Width < 40.0f || plotBounds.Height < 40.0f)
            {
                DrawCenteredMessage(graphics, "窗口空间不足", ClientRectangle);
                return;
            }

            DrawPlotArea(graphics, plotBounds);

            HistoryPoint[] currentPoints = points;
            if (currentPoints.Length == 0)
            {
                DrawCenteredMessage(
                    graphics,
                    "暂无历史数据\n运行后将显示最近约 30 分钟趋势",
                    Rectangle.Round(plotBounds));
                return;
            }

            DrawSeries(graphics, plotBounds, currentPoints, SelectCpuPercent, cpuPen);
            DrawSeries(graphics, plotBounds, currentPoints, SelectMemoryPercent, memoryPen);
            DrawSeries(graphics, plotBounds, currentPoints, SelectDiscreteGpuPercent, nvidiaPen);
            DrawSeries(graphics, plotBounds, currentPoints, SelectIntegratedGpuPercent, intelPen);
            DrawTimeRange(graphics, plotBounds, currentPoints);

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

        private float DrawLegend(Graphics graphics)
        {
            float x = OuterMargin;
            float y = OuterMargin;
            float rowHeight = Math.Max(18.0f, legendFont.GetHeight(graphics) + 4.0f);
            float rightEdge = Math.Max(OuterMargin, ClientSize.Width - OuterMargin);

            DrawLegendItem(graphics, "CPU", cpuPen, ref x, ref y, rowHeight, rightEdge);
            DrawLegendItem(graphics, "RAM", memoryPen, ref x, ref y, rowHeight, rightEdge);
            DrawLegendItem(
                graphics,
                DiscreteGpuLegendLabel,
                nvidiaPen,
                ref x,
                ref y,
                rowHeight,
                rightEdge);
            DrawLegendItem(
                graphics,
                IntegratedGpuLegendLabel,
                intelPen,
                ref x,
                ref y,
                rowHeight,
                rightEdge);

            return y + rowHeight + 4.0f;
        }

        private void DrawLegendItem(
            Graphics graphics,
            string label,
            Pen pen,
            ref float x,
            ref float y,
            float rowHeight,
            float rightEdge)
        {
            SizeF labelSize = graphics.MeasureString(label, legendFont);
            float itemWidth = 24.0f + labelSize.Width + 16.0f;

            if (x > OuterMargin && x + itemWidth > rightEdge)
            {
                x = OuterMargin;
                y += rowHeight;
            }

            float centerY = y + (rowHeight / 2.0f);
            graphics.DrawLine(pen, x, centerY, x + 16.0f, centerY);
            graphics.DrawString(
                label,
                legendFont,
                primaryTextBrush,
                x + 22.0f,
                y + ((rowHeight - labelSize.Height) / 2.0f));
            x += itemWidth;
        }

        private void DrawPlotArea(Graphics graphics, RectangleF plotBounds)
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
                            OuterMargin,
                            y - 9.0f,
                            AxisLabelWidth - 5.0f,
                            18.0f),
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
            Func<HistoryPoint, int?> selector,
            Pen pen)
        {
            float xStep = historyPoints.Length > 1
                ? plotBounds.Width / (historyPoints.Length - 1)
                : 0.0f;
            PointF previousPoint = PointF.Empty;
            int segmentLength = 0;

            for (int index = 0; index < historyPoints.Length; index++)
            {
                HistoryPoint historyPoint = historyPoints[index];
                int? value = historyPoint == null ? null : selector(historyPoint);
                if (!value.HasValue)
                {
                    DrawIsolatedPoint(graphics, pen, previousPoint, segmentLength);
                    segmentLength = 0;
                    continue;
                }

                int clampedValue = Math.Max(0, Math.Min(100, value.Value));
                PointF currentPoint = new PointF(
                    plotBounds.Left + (xStep * index),
                    ValueToY(clampedValue, plotBounds));

                if (segmentLength > 0)
                {
                    graphics.DrawLine(pen, previousPoint, currentPoint);
                }

                previousPoint = currentPoint;
                segmentLength++;
            }

            DrawIsolatedPoint(graphics, pen, previousPoint, segmentLength);
        }

        private static void DrawIsolatedPoint(
            Graphics graphics,
            Pen pen,
            PointF point,
            int segmentLength)
        {
            if (segmentLength == 1)
            {
                graphics.DrawEllipse(pen, point.X - 1.5f, point.Y - 1.5f, 3.0f, 3.0f);
            }
        }

        private void DrawTimeRange(
            Graphics graphics,
            RectangleF plotBounds,
            HistoryPoint[] historyPoints)
        {
            HistoryPoint first = FindFirstPoint(historyPoints);
            HistoryPoint last = FindLastPoint(historyPoints);
            if (first == null || last == null)
            {
                return;
            }

            DateTime localStart = first.TimestampUtc.ToLocalTime();
            DateTime localEnd = last.TimestampUtc.ToLocalTime();
            bool sameDay = localStart.Date == localEnd.Date;
            string startText = localStart.ToString(
                sameDay ? "HH:mm:ss" : "MM-dd HH:mm",
                CultureInfo.CurrentCulture);
            string endText = localEnd.ToString(
                sameDay ? "HH:mm:ss" : "MM-dd HH:mm",
                CultureInfo.CurrentCulture);
            string rangeText = FormatDuration(localEnd - localStart) + " · " +
                historyPoints.Length.ToString(CultureInfo.CurrentCulture) + " 点";
            RectangleF labelBounds = new RectangleF(
                plotBounds.Left,
                plotBounds.Bottom + 4.0f,
                plotBounds.Width,
                BottomLabelHeight - 4.0f);

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

        private static HistoryPoint FindFirstPoint(HistoryPoint[] historyPoints)
        {
            for (int index = 0; index < historyPoints.Length; index++)
            {
                if (historyPoints[index] != null)
                {
                    return historyPoints[index];
                }
            }

            return null;
        }

        private static HistoryPoint FindLastPoint(HistoryPoint[] historyPoints)
        {
            for (int index = historyPoints.Length - 1; index >= 0; index--)
            {
                if (historyPoints[index] != null)
                {
                    return historyPoints[index];
                }
            }

            return null;
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
