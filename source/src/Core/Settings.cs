using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LinkPilot.Core
{
    public enum ControlMode { Fixed = 0, Curve = 1 }

    [DataContract]
    public sealed class CurvePoint
    {
        [DataMember(Order = 1)] public double Temp { get; set; }
        [DataMember(Order = 2)] public int Percent { get; set; }

        public CurvePoint() { }
        public CurvePoint(double t, int p) { Temp = t; Percent = p; }
    }

    [DataContract]
    public sealed class ChannelSettings
    {
        /// <summary>Hub-reported device id. Stays the same if you re-cable, so settings follow the device.</summary>
        [DataMember(Order = 1)] public string DeviceId { get; set; } = "";
        [DataMember(Order = 2)] public string Nickname { get; set; } = "";
        [DataMember(Order = 3)] public ControlMode Mode { get; set; } = ControlMode.Curve;
        [DataMember(Order = 4)] public int FixedPercent { get; set; } = 50;
        [DataMember(Order = 5)] public List<CurvePoint> Curve { get; set; } = new List<CurvePoint>();
        /// <summary>Device id of the temperature sensor driving the curve ("" = auto: coolant if found).</summary>
        [DataMember(Order = 6)] public string SensorDeviceId { get; set; } = "";
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember(Order = 1)] public List<ChannelSettings> Channels { get; set; } = new List<ChannelSettings>();
        [DataMember(Order = 2)] public int PumpMinimumPercent { get; set; } = 50;
        [DataMember(Order = 3)] public double AlarmCoolantTemp { get; set; } = 45.0;
        [DataMember(Order = 4)] public bool StartWithWindows { get; set; }
        [DataMember(Order = 6)] public bool CloseToTray { get; set; } = true;
        [DataMember(Order = 7)] public bool ReturnToHardwareModeOnExit { get; set; } = true;
        [DataMember(Order = 8)] public int PollIntervalMs { get; set; } = 1000;
        [DataMember(Order = 9)] public bool DebugPacketLog { get; set; }
        /// <summary>Extra Corsair product IDs to treat as a LINK System Hub (in case a new revision ships with a new ID).</summary>
        [DataMember(Order = 10)] public List<int> ExtraHubProductIds { get; set; } = new List<int>();
        [DataMember(Order = 11)] public int PumpStallRpm { get; set; } = 300;

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c)
        {
            // DataContract skips constructors; restore defaults so older files missing fields stay sane.
            Channels = new List<ChannelSettings>();
            PumpMinimumPercent = 50;
            AlarmCoolantTemp = 45.0;
            CloseToTray = true;
            ReturnToHardwareModeOnExit = true;
            PollIntervalMs = 1000;
            ExtraHubProductIds = new List<int>();
            PumpStallRpm = 300;
        }

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkPilot");
        public static string FilePath => Path.Combine(Folder, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var s = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(fs);
                        s.Sanitize();
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Settings file unreadable, using defaults: " + ex.Message);
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                using (var ms = new MemoryStream())
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                    {
                        new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(w, this);
                    }
                    File.WriteAllBytes(tmp, ms.ToArray());
                }
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Write("Couldn't save settings: " + ex.Message);
            }
        }

        public void Sanitize()
        {
            if (Channels == null) Channels = new List<ChannelSettings>();
            if (ExtraHubProductIds == null) ExtraHubProductIds = new List<int>();
            PumpMinimumPercent = Clamp(PumpMinimumPercent, 20, 100);
            PollIntervalMs = Clamp(PollIntervalMs, 500, 5000);
            if (AlarmCoolantTemp < 30 || AlarmCoolantTemp > 70) AlarmCoolantTemp = 45;
            if (PumpStallRpm < 0) PumpStallRpm = 0;
            foreach (var c in Channels)
            {
                if (c.Curve == null || c.Curve.Count < 2) c.Curve = Curves.BalancedFan();
                c.Curve.Sort((a, b) => a.Temp.CompareTo(b.Temp));
                foreach (var p in c.Curve) p.Percent = Clamp(p.Percent, 0, 100);
                c.FixedPercent = Clamp(c.FixedPercent, 0, 100);
                if (c.Nickname == null) c.Nickname = "";
                if (c.SensorDeviceId == null) c.SensorDeviceId = "";
            }
        }

        public ChannelSettings? Find(string deviceId) => Channels.Find(c => c.DeviceId == deviceId);

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }

    public static class Curves
    {
        public static List<CurvePoint> QuietFan() => new List<CurvePoint>
            { new CurvePoint(25, 20), new CurvePoint(30, 28), new CurvePoint(35, 40), new CurvePoint(40, 65), new CurvePoint(45, 100) };

        public static List<CurvePoint> BalancedFan() => new List<CurvePoint>
            { new CurvePoint(25, 30), new CurvePoint(30, 40), new CurvePoint(35, 60), new CurvePoint(40, 85), new CurvePoint(45, 100) };

        public static List<CurvePoint> PerformanceFan() => new List<CurvePoint>
            { new CurvePoint(25, 45), new CurvePoint(30, 60), new CurvePoint(34, 80), new CurvePoint(38, 95), new CurvePoint(42, 100) };

        public static List<CurvePoint> BalancedPump() => new List<CurvePoint>
            { new CurvePoint(25, 55), new CurvePoint(30, 65), new CurvePoint(35, 80), new CurvePoint(40, 100), new CurvePoint(45, 100) };

        /// <summary>Linear interpolation between points, flat beyond the ends.</summary>
        public static double Evaluate(IList<CurvePoint> curve, double temp)
        {
            if (curve.Count == 0) return 100;
            if (temp <= curve[0].Temp) return curve[0].Percent;
            for (int i = 1; i < curve.Count; i++)
            {
                var a = curve[i - 1];
                var b = curve[i];
                if (temp <= b.Temp)
                {
                    double span = b.Temp - a.Temp;
                    if (span <= 0.0001) return b.Percent;
                    return a.Percent + (b.Percent - a.Percent) * (temp - a.Temp) / span;
                }
            }
            return curve[curve.Count - 1].Percent;
        }

        public static List<CurvePoint> Clone(IList<CurvePoint> src)
        {
            var l = new List<CurvePoint>(src.Count);
            foreach (var p in src) l.Add(new CurvePoint(p.Temp, p.Percent));
            return l;
        }
    }
}
