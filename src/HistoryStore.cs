using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TinyHwBar
{
    internal enum HistorySaveFailureStage
    {
        None = 0,
        ResolvePath = 1,
        CreateDirectory = 2,
        PreparePayload = 3,
        WriteTemporaryFile = 4,
        InspectPrimaryFile = 5,
        ReplacePrimaryFile = 6,
        MovePrimaryFile = 7
    }

    internal sealed class HistorySaveFailure
    {
        internal HistorySaveFailure(
            HistorySaveFailureStage stage,
            string exceptionType,
            int? hResult)
        {
            Stage = stage;
            ExceptionType = string.IsNullOrWhiteSpace(exceptionType)
                ? "Unknown"
                : exceptionType;
            HResult = hResult;
        }

        internal HistorySaveFailureStage Stage { get; private set; }

        internal string ExceptionType { get; private set; }

        internal int? HResult { get; private set; }

        internal string ToSafeDisplayText()
        {
            string stageText;
            switch (Stage)
            {
                case HistorySaveFailureStage.ResolvePath:
                    stageText = "解析本地历史路径";
                    break;
                case HistorySaveFailureStage.CreateDirectory:
                    stageText = "创建本地历史目录";
                    break;
                case HistorySaveFailureStage.PreparePayload:
                    stageText = "整理历史数据";
                    break;
                case HistorySaveFailureStage.WriteTemporaryFile:
                    stageText = "写入临时历史文件";
                    break;
                case HistorySaveFailureStage.InspectPrimaryFile:
                    stageText = "检查现有历史文件";
                    break;
                case HistorySaveFailureStage.ReplacePrimaryFile:
                    stageText = "替换现有历史文件";
                    break;
                case HistorySaveFailureStage.MovePrimaryFile:
                    stageText = "创建正式历史文件";
                    break;
                default:
                    stageText = "保存本地历史";
                    break;
            }

            string result = "失败阶段：" + stageText + "；错误类型：" + ExceptionType;
            if (HResult.HasValue)
            {
                result += "；HRESULT：0x" + unchecked((uint)HResult.Value).ToString(
                    "X8",
                    CultureInfo.InvariantCulture);
            }

            return result;
        }

        internal static HistorySaveFailure FromException(
            HistorySaveFailureStage stage,
            Exception exception)
        {
            if (exception == null)
            {
                return new HistorySaveFailure(stage, "Unknown", null);
            }

            return new HistorySaveFailure(
                stage,
                exception.GetType().Name,
                exception.HResult);
        }
    }

    internal sealed class HistoryStore
    {
        internal const int MaximumPointCount = MetricHistory.DefaultCapacity;
        internal static readonly TimeSpan MaximumAge = MetricHistory.MaximumAge;

        private const long MaximumFileBytes = 2L * 1024L * 1024L;
        private const string Header = "TinyHwBarHistory=1";

        private readonly object synchronization = new object();
        private readonly string historyPath;

        internal HistoryStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TinyHwBar",
                "history.csv"))
        {
        }

        internal HistoryStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("History path is required.", "path");
            }

            historyPath = path;
        }

        internal HistoryPoint[] Load()
        {
            lock (synchronization)
            {
                return LoadCore();
            }
        }

        private HistoryPoint[] LoadCore()
        {
            try
            {
                FileInfo file = new FileInfo(historyPath);
                if (!file.Exists || file.Length <= 0 || file.Length > MaximumFileBytes)
                {
                    return new HistoryPoint[0];
                }

                string[] lines = File.ReadAllLines(historyPath, Encoding.UTF8);
                if (lines.Length == 0 ||
                    !string.Equals(lines[0], Header, StringComparison.Ordinal))
                {
                    return new HistoryPoint[0];
                }

                DateTime nowUtc = DateTime.UtcNow;
                DateTime oldestAllowedUtc = nowUtc - MaximumAge;
                DateTime newestAllowedUtc = nowUtc.AddMinutes(5.0);
                List<HistoryPoint> points = new List<HistoryPoint>(MaximumPointCount);
                int firstLine = Math.Max(1, lines.Length - MaximumPointCount);

                for (int lineIndex = firstLine; lineIndex < lines.Length; lineIndex++)
                {
                    HistoryPoint point;
                    if (!TryParse(lines[lineIndex], out point) ||
                        point.TimestampUtc < oldestAllowedUtc ||
                        point.TimestampUtc > newestAllowedUtc)
                    {
                        continue;
                    }

                    points.Add(point);
                }

                points.Sort(CompareByTimestamp);
                return points.ToArray();
            }
            catch (Exception)
            {
                return new HistoryPoint[0];
            }
        }

        internal bool Save(HistoryPoint[] points)
        {
            HistorySaveFailure ignoredFailure;
            return Save(points, out ignoredFailure);
        }

        internal bool Save(
            HistoryPoint[] points,
            out HistorySaveFailure failure)
        {
            if (points == null)
            {
                throw new ArgumentNullException("points");
            }

            lock (synchronization)
            {
                return SaveCore(points, out failure);
            }
        }

        private bool SaveCore(
            HistoryPoint[] points,
            out HistorySaveFailure failure)
        {
            failure = null;
            HistorySaveFailureStage stage = HistorySaveFailureStage.ResolvePath;
            try
            {
                string directory = Path.GetDirectoryName(historyPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    failure = new HistorySaveFailure(
                        stage,
                        "InvalidPath",
                        null);
                    return false;
                }

                stage = HistorySaveFailureStage.CreateDirectory;
                Directory.CreateDirectory(directory);
                stage = HistorySaveFailureStage.PreparePayload;
                DateTime nowUtc = DateTime.UtcNow;
                DateTime oldestAllowedUtc = nowUtc - MaximumAge;
                int firstPoint = Math.Max(0, points.Length - MaximumPointCount);
                List<string> lines = new List<string>(MaximumPointCount + 1);
                lines.Add(Header);

                for (int index = firstPoint; index < points.Length; index++)
                {
                    HistoryPoint point = points[index];
                    if (point == null ||
                        point.TimestampUtc < oldestAllowedUtc ||
                        point.TimestampUtc > nowUtc.AddMinutes(5.0))
                    {
                        continue;
                    }

                    lines.Add(Serialize(point));
                }

                WriteAtomically(lines.ToArray(), ref stage);
                return true;
            }
            catch (Exception exception)
            {
                // History persistence is optional; monitoring must continue.
                failure = HistorySaveFailure.FromException(stage, exception);
                return false;
            }
        }

        internal bool Clear()
        {
            lock (synchronization)
            {
                bool primaryDeleted = TryDeleteWithResult(historyPath);
                bool backupDeleted = TryDeleteWithResult(historyPath + ".bak");
                bool temporaryFilesDeleted = TryDeleteTemporaryFilesWithResult();
                return primaryDeleted && backupDeleted && temporaryFilesDeleted;
            }
        }

        private static int CompareByTimestamp(HistoryPoint left, HistoryPoint right)
        {
            return left.TimestampUtc.CompareTo(right.TimestampUtc);
        }

        private static string Serialize(HistoryPoint point)
        {
            string[] fields =
            {
                point.TimestampUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                FormatNullable(point.CpuPercent),
                FormatNullable(point.MemoryPercent),
                FormatNullable(point.NvidiaGpuPercent),
                FormatNullable(point.VideoMemoryPercent),
                FormatNullable(point.TemperatureCelsius),
                FormatNullable(point.NetworkReceiveBytesPerSecond),
                FormatNullable(point.NetworkSendBytesPerSecond),
                FormatNullable(point.IntelGpuPercent),
                FormatNullable(point.GatewayLatencyMilliseconds)
            };

            return string.Join(",", fields);
        }

        private static bool TryParse(string line, out HistoryPoint point)
        {
            point = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string[] fields = line.Split(',');
            if (fields.Length != 9 && fields.Length != 10)
            {
                return false;
            }

            long ticks;
            int? cpu;
            int? memory;
            int? nvidia;
            int? videoMemory;
            int? temperature;
            long? receive;
            long? send;
            int? intel;
            long? gatewayLatency;

            if (!long.TryParse(
                    fields[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out ticks) ||
                ticks < DateTime.MinValue.Ticks ||
                ticks > DateTime.MaxValue.Ticks ||
                !TryParsePercent(fields[1], out cpu) ||
                !TryParsePercent(fields[2], out memory) ||
                !TryParsePercent(fields[3], out nvidia) ||
                !TryParsePercent(fields[4], out videoMemory) ||
                !TryParseTemperature(fields[5], out temperature) ||
                !TryParseNonNegativeLong(fields[6], out receive) ||
                !TryParseNonNegativeLong(fields[7], out send) ||
                !TryParsePercent(fields[8], out intel))
            {
                return false;
            }

            gatewayLatency = null;
            if (fields.Length == 10 &&
                !TryParseNonNegativeLong(fields[9], out gatewayLatency))
            {
                return false;
            }

            point = new HistoryPoint(
                new DateTime(ticks, DateTimeKind.Utc),
                cpu,
                memory,
                nvidia,
                videoMemory,
                temperature,
                receive,
                send,
                intel,
                gatewayLatency);
            return true;
        }

        private static string FormatNullable(int? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static string FormatNullable(long? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static bool TryParsePercent(string text, out int? value)
        {
            value = null;
            if (text.Length == 0)
            {
                return true;
            }

            int parsed;
            if (!int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parsed) ||
                parsed < 0 ||
                parsed > 100)
            {
                return false;
            }

            value = parsed;
            return true;
        }

        private static bool TryParseTemperature(string text, out int? value)
        {
            value = null;
            if (text.Length == 0)
            {
                return true;
            }

            int parsed;
            if (!int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parsed) ||
                parsed < -100 ||
                parsed > 250)
            {
                return false;
            }

            value = parsed;
            return true;
        }

        private static bool TryParseNonNegativeLong(string text, out long? value)
        {
            value = null;
            if (text.Length == 0)
            {
                return true;
            }

            long parsed;
            if (!long.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parsed) ||
                parsed < 0)
            {
                return false;
            }

            value = parsed;
            return true;
        }

        private void WriteAtomically(
            string[] lines,
            ref HistorySaveFailureStage stage)
        {
            string temporaryPath = historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupPath = historyPath + ".bak";

            try
            {
                stage = HistorySaveFailureStage.WriteTemporaryFile;
                File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(false));
                stage = HistorySaveFailureStage.InspectPrimaryFile;
                if (File.Exists(historyPath))
                {
                    TryDelete(backupPath);
                    stage = HistorySaveFailureStage.ReplacePrimaryFile;
                    File.Replace(temporaryPath, historyPath, backupPath, true);
                    TryDelete(backupPath);
                }
                else
                {
                    stage = HistorySaveFailureStage.MovePrimaryFile;
                    File.Move(temporaryPath, historyPath);
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Cleanup and history deletion must not terminate monitoring.
            }
        }

        private static bool TryDeleteWithResult(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return !File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool TryDeleteTemporaryFilesWithResult()
        {
            try
            {
                string directory = Path.GetDirectoryName(historyPath);
                string fileName = Path.GetFileName(historyPath);
                if (string.IsNullOrWhiteSpace(directory) ||
                    string.IsNullOrWhiteSpace(fileName) ||
                    !Directory.Exists(directory))
                {
                    return true;
                }

                string prefix = fileName + ".";
                string suffix = ".tmp";
                bool deleted = true;
                foreach (string candidate in Directory.GetFiles(
                    directory,
                    fileName + ".*.tmp",
                    SearchOption.TopDirectoryOnly))
                {
                    string candidateName = Path.GetFileName(candidate);
                    if (!candidateName.StartsWith(prefix, StringComparison.Ordinal) ||
                        !candidateName.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string token = candidateName.Substring(
                        prefix.Length,
                        candidateName.Length - prefix.Length - suffix.Length);
                    Guid parsedToken;
                    if (token.Length != 32 ||
                        !Guid.TryParseExact(token, "N", out parsedToken))
                    {
                        continue;
                    }

                    deleted = TryDeleteWithResult(candidate) && deleted;
                }

                return deleted;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
