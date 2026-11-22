using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using LinkPilot.Link;

namespace LinkPilot.Core
{
    /// <summary>Writes a file by building it next to the target and swapping it in, so readers never see half a file.</summary>
    public static class AtomicFile
    {
        public static void WriteJson<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(w, value);
                }
                File.WriteAllBytes(tmp, ms.ToArray());
            }
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }

    [DataContract]
    public sealed class StatusDevice
    {
        [DataMember(Order = 1)] public int Port { get; set; }
        [DataMember(Order = 2)] public string DeviceId { get; set; } = "";
        [DataMember(Order = 3)] public string Name { get; set; } = "";
        [DataMember(Order = 4)] public string Model { get; set; } = "";
        [DataMember(Order = 5)] public string Kind { get; set; } = "";
        [DataMember(Order = 6)] public int? Rpm { get; set; }
        [DataMember(Order = 7)] public double? TempC { get; set; }
        [DataMember(Order = 8)] public bool Controllable { get; set; }
        [DataMember(Order = 9)] public int? AppliedPercent { get; set; }
        /// <summary>"Fixed" or "Curve"; null for devices with no speed control.</summary>
        [DataMember(Order = 10)] public string? Mode { get; set; }
        [DataMember(Order = 11)] public int? FixedPercent { get; set; }
        /// <summary>What is driving the speed right now, e.g. "Curve @ 31.2°C" or "Safety override".</summary>
        [DataMember(Order = 12)] public string Driver { get; set; } = "";
        [DataMember(Order = 13)] public List<CurvePoint>? Curve { get; set; }
    }

    /// <summary>
    /// Everything LinkPilot knows right now, in one JSON file (%APPDATA%\LinkPilot\status.json).
    /// Meant for scripts and AI assistants that want to read the loop's state without touching the hub.
    /// If <see cref="Updated"/> is more than a few seconds old while <see cref="Running"/> is true, LinkPilot has died.
    /// </summary>
    [DataContract]
    public sealed class StatusDoc
    {
        [DataMember(Order = 1)] public string Updated { get; set; } = "";
        [DataMember(Order = 2)] public bool Running { get; set; }
        [DataMember(Order = 3)] public string Version { get; set; } = "";
        [DataMember(Order = 4)] public string ExePath { get; set; } = "";
        [DataMember(Order = 5)] public bool Connected { get; set; }
        [DataMember(Order = 6)] public string Status { get; set; } = "";
        [DataMember(Order = 7)] public string Firmware { get; set; } = "";
        [DataMember(Order = 8)] public double? CoolantC { get; set; }
        [DataMember(Order = 9)] public string CoolantSource { get; set; } = "";
        [DataMember(Order = 10)] public int? PumpRpm { get; set; }
        [DataMember(Order = 11)] public bool FullBlast { get; set; }
        [DataMember(Order = 12)] public string? Alarm { get; set; }
        [DataMember(Order = 13)] public string LockInfo { get; set; } = "";
        [DataMember(Order = 14)] public int PumpMinimumPercent { get; set; }
        [DataMember(Order = 15)] public double AlarmCoolantC { get; set; }
        [DataMember(Order = 16)] public int PumpStallRpm { get; set; }
        [DataMember(Order = 17)] public int PollIntervalMs { get; set; }
        [DataMember(Order = 18)] public bool ReturnToHardwareModeOnExit { get; set; }
        [DataMember(Order = 19)] public List<StatusDevice> Devices { get; set; } = new List<StatusDevice>();
        [DataMember(Order = 20)] public List<string> RecentLog { get; set; } = new List<string>();
    }

    public static class StatusFile
    {
        public static string FilePath => Path.Combine(AppSettings.Folder, "status.json");
        public static string HistoryPath => Path.Combine(AppSettings.Folder, "history.csv");

        public const string HistoryHeader = "time,coolant_c,pump_rpm,pump_percent,fan_avg_rpm,fan_avg_percent,fan_max_percent,alarm";
        private const long HistoryMaxBytes = 2 * 1024 * 1024;

        public static StatusDoc Build(HubSnapshot snap, AppSettings settings, bool running, DateTimeOffset now)
        {
            var doc = new StatusDoc
            {
                Updated = now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                Running = running,
                Version = typeof(StatusFile).Assembly.GetName().Version.ToString(3),
                ExePath = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "",
                Connected = snap.Connected,
                Status = snap.Status,
                Firmware = snap.Firmware,
                CoolantC = Round1(snap.CoolantTemp),
                CoolantSource = snap.CoolantSource,
                PumpRpm = snap.PumpRpm,
                FullBlast = snap.FullBlast,
                Alarm = snap.Alarm,
                LockInfo = snap.LockInfo,
                PumpMinimumPercent = settings.PumpMinimumPercent,
                AlarmCoolantC = settings.AlarmCoolantTemp,
                PumpStallRpm = settings.PumpStallRpm,
                PollIntervalMs = settings.PollIntervalMs,
                ReturnToHardwareModeOnExit = settings.ReturnToHardwareModeOnExit,
            };

            foreach (var ch in snap.Channels.OrderBy(c => c.Channel))
            {
                var st = ch.Controllable ? settings.Find(ch.DeviceId) : null;
                doc.Devices.Add(new StatusDevice
                {
                    Port = ch.Channel,
                    DeviceId = ch.DeviceId,
                    Name = ch.DisplayName,
                    Model = ch.Model.Name,
                    Kind = ch.Model.Kind.ToString(),
                    Rpm = ch.Rpm,
                    TempC = Round1(ch.Temp),
                    Controllable = ch.Controllable,
                    AppliedPercent = ch.Controllable ? (int?)ch.AppliedPercent : null,
                    Mode = st == null ? null : st.Mode.ToString(),
                    FixedPercent = st == null ? (int?)null : st.FixedPercent,
                    Driver = ch.DriverNote,
                    Curve = st == null ? null : Curves.Clone(st.Curve),
                });
            }

            var log = Log.RecentLines();
            doc.RecentLog = log.Skip(Math.Max(0, log.Length - 25)).ToList();
            return doc;
        }

        public static void Write(StatusDoc doc, string? path = null)
        {
            AtomicFile.WriteJson(path ?? FilePath, doc);
        }

        /// <summary>One CSV line for the history file, or null when there's nothing worth recording.</summary>
        public static string? HistoryRow(HubSnapshot snap, DateTimeOffset now)
        {
            if (!snap.Connected)
            {
                return null;
            }

            var inv = CultureInfo.InvariantCulture;
            var pump = snap.Channels.FirstOrDefault(c => c.Model.Kind == LinkKind.Pump && c.Controllable);
            var fans = snap.Channels.Where(c => c.Model.Kind == LinkKind.Fan && c.Controllable).ToList();
            var fanRpms = fans.Where(f => f.Rpm.HasValue).Select(f => (double)f.Rpm!.Value).ToList();

            return string.Join(",", new[]
            {
                now.ToString("yyyy-MM-ddTHH:mm:sszzz", inv),
                snap.CoolantTemp.HasValue ? snap.CoolantTemp.Value.ToString("0.0", inv) : "",
                pump?.Rpm != null ? pump.Rpm.Value.ToString(inv) : "",
                pump != null ? pump.AppliedPercent.ToString(inv) : "",
                fanRpms.Count > 0 ? fanRpms.Average().ToString("0", inv) : "",
                fans.Count > 0 ? fans.Average(f => f.AppliedPercent).ToString("0", inv) : "",
                fans.Count > 0 ? fans.Max(f => f.AppliedPercent).ToString(inv) : "",
                snap.Alarm == null ? "" : snap.Alarm.Replace(',', ';'),
            });
        }

        public static void AppendHistory(string row, string? path = null)
        {
            path = path ?? HistoryPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > HistoryMaxBytes)
            {
                var old = Path.ChangeExtension(path, ".old.csv");
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
                fi = new FileInfo(path);
            }
            if (!fi.Exists)
            {
                File.WriteAllText(path, HistoryHeader + Environment.NewLine);
            }
            File.AppendAllText(path, row + Environment.NewLine);
        }

        private static double? Round1(double? v) => v.HasValue ? (double?)Math.Round(v.Value, 1) : null;
    }
}
