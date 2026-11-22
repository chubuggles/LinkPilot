using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LinkPilot.Core;
using LinkPilot.Hid;
using LinkPilot.Link;

// Runs on any machine (no hardware): checks LinkPilot's parsers against real iCUE LINK hub captures
// published in the FanControl.CorsairLink test suite, and drives LinkHub against a simulated hub.
public static class ProtocolTests
{
    static int _fail, _pass;

    static void Check(bool cond, string what)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL: " + what); }
    }

    static void WaitFor(Func<bool> cond, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond() && DateTime.UtcNow < until) System.Threading.Thread.Sleep(50);
    }

    static byte[] H(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    public static int Main(string[] args)
    {
        var v = File.ReadAllLines(args.Length > 0 ? args[0] : "vectors.txt").Where(l => l.Length > 0 && !l.StartsWith("#")).Select(H).ToArray();

        // --- device lists
        var d = LinkProtocol.ParseSubDevices(v[0]);
        Check(d.Count == 6, "vec0 count 6 (got " + d.Count + ")");
        Check(d.First(x => x.Channel == 10).Model == 0x07, "vec0 ch10 is H-series AIO");

        d = LinkProtocol.ParseSubDevices(v[1]);
        Check(d.Count == 8, "vec1 count 8");
        Check(d.First(x => x.Channel == 10).Variant == 0x05, "vec1 ch10 variant 5");

        d = LinkProtocol.ParseSubDevices(v[2]);
        Check(d.Count == 4, "vec2 count 4");
        Check(d.First(x => x.Channel == 8).Model == 0x09, "vec2 ch8 XC7");

        d = LinkProtocol.ParseSubDevices(v[3]);
        Check(d.Count == 3, "vec3 count 3");
        Check(d.First(x => x.Channel == 1).Model == 0x0c, "vec3 ch1 XD5");
        Check(d.First(x => x.Channel == 2).Model == 0x0e, "vec3 ch2 XD5 Elite LCD");
        Check(d.First(x => x.Channel == 13).Model == 0x09, "vec3 ch13 XC7");
        Check(d.All(x => x.Id.Length > 0), "vec3 ids parsed");

        d = LinkProtocol.ParseSubDevices(v[4], v[5]);
        Check(d.Count == 15, "cont1 count 15 (got " + d.Count + ")");
        Check(d.Select(x => x.Channel).Distinct().Count() == d.Count, "cont1 unique channels");
        Check(d.First(x => x.Channel == 15).Model == 0x02, "cont1 ch15 LX");

        d = LinkProtocol.ParseSubDevices(v[6], v[7]);
        Check(d.Count == 18, "cont2 count 18 (got " + d.Count + ")");
        Check(d.First(x => x.Channel == 6).Model == 0x13, "cont2 ch6 RX");

        d = LinkProtocol.ParseSubDevices(v[8], v[9]);
        Check(d.Count == 13, "cont3 count 13 (got " + d.Count + ")");

        // --- firmware / speeds / temps
        Check(LinkProtocol.ParseFirmware(v[10]) == "2.9.488", "firmware 2.9.488");
        var sp = LinkProtocol.ParseSpeeds(v[11]);
        Check(sp.Count == 15, "speeds count 15");
        Check(!sp[0].Available, "speed ch0 unavailable");
        Check(sp[1].Available && (int)sp[1].Value == 482, "speed ch1 = 482 rpm");
        var tp = LinkProtocol.ParseTemperatures(v[12]);
        Check(tp.Count == 15, "temps count 15");
        Check(tp[1].Available && Math.Abs(tp[1].Value - 20.8) < 0.05, "temp ch1 = 20.8C");

        // --- packet builders
        var cmd = LinkProtocol.BuildCommand(513, LinkProtocol.CmdOpenEndpoint, LinkProtocol.EpSpeeds);
        Check(cmd.Length == 513 && cmd[0] == 0 && cmd[1] == 0 && cmd[2] == 1 && cmd[3] == 0x0d && cmd[4] == 0x01 && cmd[5] == 0x17, "open-endpoint packet layout");
        var speeds = LinkProtocol.BuildFixedPercentSpeeds(new Dictionary<int, byte> { { 3, 40 }, { 1, 70 } });
        Check(speeds.SequenceEqual(new byte[] { 2, 1, 0, 70, 0, 3, 0, 40, 0 }), "speed table sorted + layout");
        var wd = LinkProtocol.BuildWriteData(LinkProtocol.DtSetSpeeds, speeds);
        Check(wd[0] == 11 && wd[1] == 0 && wd[2] == 0 && wd[3] == 0 && wd[4] == 0x07 && wd[5] == 0x00 && wd[6] == 2, "write wrapper header");
        Check(LinkProtocol.FirmwareSupportsTwoPageDeviceList("2.5.0") && !LinkProtocol.FirmwareSupportsTwoPageDeviceList("2.4.9") && LinkProtocol.FirmwareSupportsTwoPageDeviceList("3.12.650"), "fw paging check");

        // --- curves
        var c = Curves.BalancedFan();
        Check(Curves.Evaluate(c, 10) == 30, "curve clamps low");
        Check(Curves.Evaluate(c, 99) == 100, "curve clamps high");
        Check(Math.Abs(Curves.Evaluate(c, 32.5) - 50) < 0.001, "curve interpolates");

        // --- full hub conversation against a simulated hub
        var fake = new FakeHub(v);
        using (var guard = new CorsairGuard())
        {
            var hub = new LinkHub(fake, guard);
            hub.Initialize();
            Check(hub.Firmware == "2.9.488", "hub firmware via transport");
            Check(fake.SoftwareModeRequested, "entered software mode");
            var devs = hub.GetSubDevices();
            Check(devs.Count == 3, "hub device list via transport (got " + devs.Count + ")");
            var rpm = hub.GetSpeeds();
            Check(rpm[1].Available && (int)rpm[1].Value == 482, "hub speeds via transport (skips stale report)");
            var temps = hub.GetTemperatures();
            Check(Math.Abs(temps[1].Value - 20.8) < 0.05, "hub temps via transport");
            hub.SetFixedPercent(new Dictionary<int, byte> { { 1, 60 }, { 2, 35 } });
            Check(fake.LastWriteBody != null && fake.LastWriteBody.Take(15).SequenceEqual(new byte[] { 0x0b, 0, 0, 0, 0x07, 0x00, 2, 1, 0, 60, 0, 2, 0, 35, 0 }), "speed write payload on the wire");
            Check(fake.EndpointBalanced, "every opened endpoint was closed");

            fake.FailNextWithWrongMode = true;
            try { hub.GetSpeeds(); Check(false, "wrong-mode should throw"); }
            catch (LinkCommandException ex) { Check(ex.StatusCode == 0x03, "wrong-mode surfaces status 3"); }
        }

        // --- settings round trip
        var tmp = Path.Combine(Path.GetTempPath(), "lp-settings-test.json");
        var s = new AppSettings();
        s.Channels.Add(new ChannelSettings { DeviceId = "abc", Mode = ControlMode.Fixed, FixedPercent = 77, Curve = Curves.QuietFan() });
        using (var ms = new MemoryStream())
        {
            new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(AppSettings)).WriteObject(ms, s);
            ms.Position = 0;
            var back = (AppSettings)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(AppSettings)).ReadObject(ms);
            back.Sanitize();
            Check(back.Find("abc")?.FixedPercent == 77 && back.Find("abc")?.Mode == ControlMode.Fixed && back.PumpMinimumPercent == 50, "settings json round trip");
        }
        var partial = System.Text.Encoding.UTF8.GetBytes("{\"Channels\":[{\"DeviceId\":\"x\"}]}");
        using (var ms = new MemoryStream(partial))
        {
            var back = (AppSettings)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(AppSettings)).ReadObject(ms);
            back.Sanitize();
            Check(back.PumpMinimumPercent == 50 && back.AlarmCoolantTemp == 45 && back.Find("x")!.Curve.Count == 5, "old/partial settings get safe defaults");
        }

        // --- status file (what scripts and AI assistants read)
        var snap = new HubSnapshot
        {
            Connected = true,
            Status = "Connected · 3 LINK devices",
            Firmware = "4.1.656",
            CoolantTemp = 31.26,
            CoolantSource = "XD6 Pump/Res",
            PumpRpm = 2100,
            Channels = new List<ChannelState>
            {
                new ChannelState { Channel = 2, DeviceId = "fanA", Model = LinkModels.Lookup(0x02), Rpm = 800, Controllable = true, AppliedPercent = 40, DriverNote = "Curve @ 31.3°C", DisplayName = "Top fan" },
                new ChannelState { Channel = 1, DeviceId = "pump", Model = LinkModels.Lookup(0x19), Rpm = 2100, Temp = 31.26, Controllable = true, AppliedPercent = 30, DriverNote = "Fixed", DisplayName = "XD6 Pump/Res" },
                new ChannelState { Channel = 3, DeviceId = "fanB", Model = LinkModels.Lookup(0x02), Rpm = 1000, Controllable = true, AppliedPercent = 60, DriverNote = "Curve @ 31.3°C", DisplayName = "LX RGB Fan" },
            },
        };
        var cfg = new AppSettings { PumpMinimumPercent = 20 };
        cfg.Channels.Add(new ChannelSettings { DeviceId = "pump", Mode = ControlMode.Fixed, FixedPercent = 30, Curve = Curves.BalancedPump() });
        cfg.Channels.Add(new ChannelSettings { DeviceId = "fanA", Mode = ControlMode.Curve, Curve = Curves.QuietFan() });
        var when = new DateTimeOffset(2026, 11, 22, 9, 30, 0, TimeSpan.FromHours(-7));

        var doc = StatusFile.Build(snap, cfg, true, when);
        Check(doc.Updated == "2026-11-22T09:30:00-07:00", "status timestamp format (got " + doc.Updated + ")");
        Check(doc.Running && doc.Connected && doc.CoolantC == 31.3 && doc.PumpRpm == 2100 && doc.PumpMinimumPercent == 20, "status header values");
        Check(doc.Devices.Count == 3 && doc.Devices[0].Port == 1 && doc.Devices[0].Kind == "Pump", "status devices sorted by port");
        Check(doc.Devices[0].Mode == "Fixed" && doc.Devices[0].FixedPercent == 30 && doc.Devices[0].AppliedPercent == 30 && doc.Devices[0].TempC == 31.3, "status pump details");
        Check(doc.Devices[1].Mode == "Curve" && doc.Devices[1].Curve!.Count == 5 && doc.Devices[1].Name == "Top fan", "status fan details");
        Check(doc.Devices[2].Mode == null && doc.Devices[2].Curve == null, "status device with no saved settings");

        var statusPath = Path.Combine(Path.GetTempPath(), "lp-status-test.json");
        File.Delete(statusPath);
        StatusFile.Write(doc, statusPath);
        StatusFile.Write(doc, statusPath); // second write replaces an existing file
        using (var fs = File.OpenRead(statusPath))
        {
            var back = (StatusDoc)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(StatusDoc)).ReadObject(fs);
            Check(back.Devices.Count == 3 && back.Devices[1].Rpm == 800 && back.Devices[2].TempC == null && back.Firmware == "4.1.656", "status json round trip");
        }
        Check(!File.Exists(statusPath + ".tmp"), "status write leaves no temp file");
        File.Delete(statusPath);

        var row = StatusFile.HistoryRow(snap, when);
        Check(row == "2026-11-22T09:30:00-07:00,31.3,2100,30,900,50,60,", "history row (got " + row + ")");
        Check(StatusFile.HistoryRow(new HubSnapshot(), when) == null, "no history row while disconnected");
        var histPath = Path.Combine(Path.GetTempPath(), "lp-history-test.csv");
        File.Delete(histPath);
        StatusFile.AppendHistory(row!, histPath);
        StatusFile.AppendHistory(row!, histPath);
        var hist = File.ReadAllLines(histPath);
        Check(hist.Length == 3 && hist[0] == StatusFile.HistoryHeader && hist[2] == row, "history file has a header then rows");
        File.Delete(histPath);

        var stopped = StatusFile.Build(new HubSnapshot { Connected = false, Status = "LinkPilot is not running" }, cfg, false, when);
        Check(!stopped.Running && !stopped.Connected && stopped.Devices.Count == 0 && stopped.CoolantC == null, "stopped status");

        // --- the whole control loop against the simulated hub, writing its files to a scratch folder
        var dataDir = Path.Combine(Path.GetTempPath(), "lp-loop-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LINKPILOT_DATA_DIR", dataDir);
        try
        {
            var loopHub = new FakeHub(v);
            var loopCfg = new AppSettings { PollIntervalMs = 500, PumpMinimumPercent = 50 };
            var ctl = new Controller(loopCfg, () => loopHub);
            ctl.Start();
            WaitFor(() => ctl.Latest.Connected && loopHub.LastWriteBody != null, 6000);
            Check(ctl.Latest.Connected && ctl.Latest.Channels.Count == 3, "loop connects and lists devices");
            Check(loopHub.LastWriteBody != null, "loop writes speeds to the hub");
            var loopPump = ctl.Latest.Channels.FirstOrDefault(x => x.Channel == 1);
            Check(loopPump != null && loopPump.Model.Kind == LinkKind.Pump && loopPump.AppliedPercent >= 50, "loop keeps the pump at or above its floor");
            Check(ctl.Latest.CoolantTemp.HasValue, "loop finds a coolant temperature");

            int modeCallsBefore = loopHub.SoftwareModeCount;
            loopHub.FailNextWithWrongMode = true;
            WaitFor(() => loopHub.SoftwareModeCount > modeCallsBefore, 5000);
            Check(loopHub.SoftwareModeCount > modeCallsBefore, "loop retakes software mode after another app drops it");
            System.Threading.Thread.Sleep(1200);
            Check(ctl.Latest.Connected, "loop stays connected after the mode drop");

            ctl.Edit(x => { var p = x.Find(loopPump!.DeviceId); if (p != null) { p.Mode = ControlMode.Fixed; p.FixedPercent = 10; } });
            System.Threading.Thread.Sleep(1500);
            Check(ctl.Latest.Channels.First(x => x.Channel == 1).AppliedPercent >= 50, "a fixed pump speed below the floor is raised to the floor");

            WaitFor(() => File.Exists(Path.Combine(dataDir, "status.json")) && File.Exists(Path.Combine(dataDir, "history.csv")), 5000);
            StatusDoc live;
            using (var fs = File.OpenRead(Path.Combine(dataDir, "status.json")))
            {
                live = (StatusDoc)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(StatusDoc)).ReadObject(fs);
            }
            Check(live.Running && live.Connected && live.Devices.Count == 3 && live.RecentLog.Count > 0, "loop publishes status.json");
            Check(File.ReadAllLines(Path.Combine(dataDir, "history.csv")).Length >= 2, "loop publishes history.csv");
            Check(File.Exists(Path.Combine(dataDir, "settings.json")), "loop saves settings for newly found devices");

            ctl.Dispose();
            Check(loopHub.HardwareModeRequested, "exit hands the hub back to hardware mode");
            using (var fs = File.OpenRead(Path.Combine(dataDir, "status.json")))
            {
                live = (StatusDoc)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(StatusDoc)).ReadObject(fs);
            }
            Check(!live.Running && !live.Connected, "exit marks status.json as not running");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LINKPILOT_DATA_DIR", null);
            try { Directory.Delete(dataDir, true); } catch { }
        }

        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>Simulated LINK hub that answers like the real one, using the captured responses.</summary>
    sealed class FakeHub : IHidTransport
    {
        readonly byte[][] _v;
        readonly Queue<byte[]> _out = new Queue<byte[]>();
        byte _openEp;
        int _deviceReads;
        int _openCount;
        public bool SoftwareModeRequested;
        public bool HardwareModeRequested;
        public volatile int SoftwareModeCount;
        public volatile byte[]? LastWriteBody;
        public volatile bool FailNextWithWrongMode;
        public bool EndpointBalanced => _openCount == 0;

        public FakeHub(byte[][] v) { _v = v; }
        public int OutputReportLength => 513;
        public int InputReportLength => 513;
        public void FlushInput() { _out.Clear(); }
        public void Dispose() { }

        static byte[] Report(byte[] payload)
        {
            var r = new byte[513];
            Buffer.BlockCopy(payload, 0, r, 1, Math.Min(payload.Length, 512));
            return r;
        }

        static byte[] Ok() => Report(new byte[] { 0x00, 0x00, 0x00, 0x00 });

        public void Write(byte[] report)
        {
            if (report.Length != 513 || report[2] != 0x01) throw new IOException("bad report framing");
            byte c0 = report[3], c1 = report[4];

            if (FailNextWithWrongMode) { FailNextWithWrongMode = false; _out.Enqueue(Report(new byte[] { 0, 0, 0, 0x03 })); return; }

            if (c0 == 0x02 && c1 == 0x13) { _out.Enqueue(Report(_v[10])); return; }
            if (c0 == 0x01 && c1 == 0x03) {
                if (report[6] == 0x02) { SoftwareModeRequested = true; SoftwareModeCount++; }
                if (report[6] == 0x01) HardwareModeRequested = true;
                _out.Enqueue(Ok());
                return;
            }
            if (c0 == 0x0d && c1 == 0x01) { _openEp = report[5]; _deviceReads = 0; _openCount++; _out.Enqueue(Ok()); return; }
            if (c0 == 0x05 && c1 == 0x01 && report[5] == 0x01)
            {
                if (_openEp == report[6] && _openCount > 0) { _openCount--; _openEp = 0; }
                _out.Enqueue(Ok());
                return;
            }
            if (c0 == 0x08 && c1 == 0x01)
            {
                switch (_openEp)
                {
                    case 0x36:
                        _out.Enqueue(Report(_deviceReads++ == 0 ? _v[3] : new byte[] { 0, 0, 0, 0 }));
                        break;
                    case 0x17:
                        _out.Enqueue(Report(_v[12])); // a stale temperature report first…
                        _out.Enqueue(Report(_v[11])); // …then the speeds we asked for
                        break;
                    case 0x21:
                        _out.Enqueue(Report(_v[12]));
                        break;
                    default:
                        throw new IOException("read with no endpoint open");
                }
                return;
            }
            if (c0 == 0x06 && c1 == 0x01)
            {
                if (_openEp != 0x18) throw new IOException("write to wrong endpoint");
                LastWriteBody = report.Skip(5).Take(64).ToArray();
                _out.Enqueue(Ok());
                return;
            }
            throw new IOException($"unexpected command {c0:x2} {c1:x2}");
        }

        public bool Read(byte[] buffer, int timeoutMs)
        {
            if (_out.Count == 0) return false;
            var r = _out.Dequeue();
            Array.Clear(buffer, 0, buffer.Length);
            Buffer.BlockCopy(r, 0, buffer, 0, Math.Min(r.Length, buffer.Length));
            return true;
        }
    }
}
