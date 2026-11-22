using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LinkPilot.Hid
{
    /// <summary>
    /// A pretend iCUE LINK System Hub (one XD6 pump/res and three LX fans) for `LinkPilot.exe --demo`.
    /// It answers the same commands as the real hub, so the whole app can be tried, and its window
    /// checked, without any hardware and without disturbing a real loop.
    /// </summary>
    public sealed class DemoHub : IHidTransport
    {
        private static readonly byte[] Models = { 0x19, 0x02, 0x02, 0x02 }; // channel 1..4
        private readonly Queue<byte[]> _out = new Queue<byte[]>();
        private readonly int[] _percent = { 0, 50, 40, 40, 40 };            // index = channel
        private readonly DateTime _started = DateTime.UtcNow;
        private byte _openEndpoint;
        private bool _softwareMode;

        public int OutputReportLength => 513;
        public int InputReportLength => 513;
        public void FlushInput() { _out.Clear(); }
        public void Dispose() { }

        private static byte[] Report(params byte[] payload)
        {
            var r = new byte[513];
            Buffer.BlockCopy(payload, 0, r, 1, Math.Min(payload.Length, 512));
            return r;
        }

        private static byte[] Status(byte status) => Report(0x00, 0x00, 0x00, status);

        public void Write(byte[] report)
        {
            if (report.Length != 513 || report[2] != 0x01) throw new IOException("bad report framing");
            byte c0 = report[3], c1 = report[4];

            if (c0 == 0x02 && c1 == 0x13) { _out.Enqueue(Report(0, 0, 0, 0, 4, 1, 0x90, 0x02)); return; }          // firmware 4.1.656
            if (c0 == 0x01 && c1 == 0x03) { _softwareMode = report[6] == 0x02; _out.Enqueue(Status(0)); return; }
            if (!_softwareMode) { _out.Enqueue(Status(0x03)); return; }
            if (c0 == 0x0d && c1 == 0x01) { _openEndpoint = report[5]; _out.Enqueue(Status(0)); return; }
            if (c0 == 0x05 && c1 == 0x01) { _openEndpoint = 0; _out.Enqueue(Status(0)); return; }
            if (c0 == 0x08 && c1 == 0x01) { _out.Enqueue(Report(Read())); return; }
            if (c0 == 0x06 && c1 == 0x01)
            {
                if (_openEndpoint != 0x18) throw new IOException("write to wrong endpoint");
                int count = report[11];
                for (int i = 0; i < count; i++)
                {
                    int ch = report[12 + i * 4];
                    if (ch >= 1 && ch < _percent.Length) _percent[ch] = Math.Min(100, (int)report[14 + i * 4]);
                }
                _out.Enqueue(Status(0));
                return;
            }
            throw new IOException($"unexpected command {c0:x2} {c1:x2}");
        }

        private byte[] Read()
        {
            var p = new List<byte> { 0, 0, 0, 0 };
            switch (_openEndpoint)
            {
                case 0x36: // device list
                    p.AddRange(new byte[] { 0x21, 0x00, (byte)Models.Length });
                    for (int ch = 1; ch <= Models.Length; ch++)
                    {
                        var id = Encoding.ASCII.GetBytes("DEMO0000000000000000000" + ch.ToString("000"));
                        p.AddRange(new byte[] { 0, 0, Models[ch - 1], 0, 0, 0, 0, (byte)id.Length });
                        p.AddRange(id);
                    }
                    break;
                case 0x17: // speeds
                    p.AddRange(new byte[] { 0x25, 0x00, (byte)(Models.Length + 1), 1, 0, 0 });
                    for (int ch = 1; ch <= Models.Length; ch++)
                    {
                        int rpm = Models[ch - 1] == 0x19 ? _percent[ch] * 48 + 250 : _percent[ch] * 22 + 30;
                        p.AddRange(new byte[] { 0, (byte)(rpm & 0xff), (byte)(rpm >> 8) });
                    }
                    break;
                case 0x21: // temperatures: only the pump/res has a coolant sensor; it drifts slowly between 28 and 36 C
                    double t = 32 + 4 * Math.Sin((DateTime.UtcNow - _started).TotalSeconds / 20.0);
                    int tenths = (int)Math.Round(t * 10);
                    p.AddRange(new byte[] { 0x10, 0x00, (byte)(Models.Length + 1), 1, 0, 0, 0, (byte)(tenths & 0xff), (byte)(tenths >> 8) });
                    for (int ch = 2; ch <= Models.Length; ch++) p.AddRange(new byte[] { 1, 0, 0 });
                    break;
                default:
                    throw new IOException("read with no endpoint open");
            }
            return p.ToArray();
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
