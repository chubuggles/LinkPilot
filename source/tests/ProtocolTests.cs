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
        public byte[]? LastWriteBody;
        public bool FailNextWithWrongMode;
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
            if (c0 == 0x01 && c1 == 0x03) { SoftwareModeRequested |= report[6] == 0x02; _out.Enqueue(Ok()); return; }
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
