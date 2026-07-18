using System;

namespace TinyHwBar
{
    internal sealed class HistoryPoint
    {
        internal HistoryPoint(
            DateTime timestampUtc,
            int? cpuPercent,
            int? memoryPercent,
            int? nvidiaGpuPercent,
            int? videoMemoryPercent,
            int? temperatureCelsius,
            long? networkReceiveBytesPerSecond,
            long? networkSendBytesPerSecond,
            int? intelGpuPercent,
            long? gatewayLatencyMilliseconds)
        {
            TimestampUtc = NormalizeUtc(timestampUtc);
            CpuPercent = cpuPercent;
            MemoryPercent = memoryPercent;
            NvidiaGpuPercent = nvidiaGpuPercent;
            VideoMemoryPercent = videoMemoryPercent;
            TemperatureCelsius = temperatureCelsius;
            NetworkReceiveBytesPerSecond = networkReceiveBytesPerSecond;
            NetworkSendBytesPerSecond = networkSendBytesPerSecond;
            IntelGpuPercent = intelGpuPercent;
            GatewayLatencyMilliseconds = gatewayLatencyMilliseconds;
        }

        internal DateTime TimestampUtc { get; private set; }

        internal int? CpuPercent { get; private set; }

        internal int? MemoryPercent { get; private set; }

        internal int? NvidiaGpuPercent { get; private set; }

        // The on-disk history schema keeps its original column order for compatibility.
        internal int? DiscreteGpuPercent { get { return NvidiaGpuPercent; } }

        internal int? VideoMemoryPercent { get; private set; }

        internal int? TemperatureCelsius { get; private set; }

        internal long? NetworkReceiveBytesPerSecond { get; private set; }

        internal long? NetworkSendBytesPerSecond { get; private set; }

        internal int? IntelGpuPercent { get; private set; }

        internal int? IntegratedGpuPercent { get { return IntelGpuPercent; } }

        internal long? GatewayLatencyMilliseconds { get; private set; }

        internal static HistoryPoint FromSnapshot(HardwareSnapshot snapshot)
        {
            return FromSnapshot(snapshot, null, null, null);
        }

        internal static HistoryPoint FromSnapshot(
            HardwareSnapshot snapshot,
            long? networkReceiveBytesPerSecond,
            long? networkSendBytesPerSecond,
            int? intelGpuPercent)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            return new HistoryPoint(
                snapshot.SampledAtUtc,
                snapshot.CpuPercent,
                snapshot.MemoryPercent,
                snapshot.GpuPercent,
                snapshot.VideoMemoryPercent,
                snapshot.TemperatureCelsius,
                networkReceiveBytesPerSecond,
                networkSendBytesPerSecond,
                intelGpuPercent,
                snapshot.GatewayLatencyMilliseconds);
        }

        private static DateTime NormalizeUtc(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Utc)
            {
                return timestamp;
            }

            if (timestamp.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            }

            return timestamp.ToUniversalTime();
        }
    }

    internal sealed class MetricHistory
    {
        internal const int DefaultCapacity = 900;
        internal static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24.0);

        private readonly object synchronization = new object();
        private readonly HistoryPoint[] points;

        private int firstIndex;
        private int count;

        internal MetricHistory()
            : this(DefaultCapacity)
        {
        }

        internal MetricHistory(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException("capacity");
            }

            points = new HistoryPoint[capacity];
        }

        internal int Count
        {
            get
            {
                lock (synchronization)
                {
                    RemoveExpiredCore(DateTime.UtcNow);
                    return count;
                }
            }
        }

        internal void Add(HardwareSnapshot snapshot)
        {
            Add(HistoryPoint.FromSnapshot(snapshot));
        }

        internal void Add(
            HardwareSnapshot snapshot,
            long? networkReceiveBytesPerSecond,
            long? networkSendBytesPerSecond,
            int? intelGpuPercent)
        {
            Add(HistoryPoint.FromSnapshot(
                snapshot,
                networkReceiveBytesPerSecond,
                networkSendBytesPerSecond,
                intelGpuPercent));
        }

        internal void Add(HistoryPoint point)
        {
            if (point == null)
            {
                throw new ArgumentNullException("point");
            }

            lock (synchronization)
            {
                RemoveExpiredCore(DateTime.UtcNow);
                if (count == points.Length)
                {
                    points[firstIndex] = point;
                    firstIndex = (firstIndex + 1) % points.Length;
                    return;
                }

                int insertionIndex = (firstIndex + count) % points.Length;
                points[insertionIndex] = point;
                count++;
            }
        }

        internal HistoryPoint[] Snapshot()
        {
            return Snapshot(DateTime.UtcNow);
        }

        internal HistoryPoint[] Snapshot(DateTime nowUtc)
        {
            lock (synchronization)
            {
                RemoveExpiredCore(NormalizeUtc(nowUtc));
                HistoryPoint[] snapshot = new HistoryPoint[count];
                for (int index = 0; index < count; index++)
                {
                    snapshot[index] = points[(firstIndex + index) % points.Length];
                }

                return snapshot;
            }
        }

        internal void Clear()
        {
            lock (synchronization)
            {
                Array.Clear(points, 0, points.Length);
                firstIndex = 0;
                count = 0;
            }
        }

        private void RemoveExpiredCore(DateTime nowUtc)
        {
            DateTime oldestAllowedUtc = nowUtc - MaximumAge;
            int originalCount = count;
            int retainedCount = 0;

            for (int index = 0; index < originalCount; index++)
            {
                int sourceIndex = (firstIndex + index) % points.Length;
                HistoryPoint point = points[sourceIndex];
                if (point == null || point.TimestampUtc < oldestAllowedUtc)
                {
                    continue;
                }

                int destinationIndex = (firstIndex + retainedCount) % points.Length;
                points[destinationIndex] = point;
                retainedCount++;
            }

            for (int index = retainedCount; index < originalCount; index++)
            {
                points[(firstIndex + index) % points.Length] = null;
            }

            count = retainedCount;
            if (count == 0)
            {
                firstIndex = 0;
            }
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            if (value.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }

            return value.ToUniversalTime();
        }
    }
}
