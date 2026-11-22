using System;
using System.Collections.Generic;
using System.IO;
using LinkPilot.Hid;

namespace LinkPilot.Link
{
    public sealed class LinkCommandException : IOException
    {
        public byte StatusCode { get; }
        public LinkCommandException(string message, byte status) : base(message) { StatusCode = status; }
    }

    /// <summary>Thrown when another app held the shared Corsair lock too long; just try again next tick.</summary>
    public sealed class LinkBusyException : IOException
    {
        public LinkBusyException() : base("Corsair device lock is busy (another app is talking to the hub).") { }
    }

    /// <summary>Talks to one iCUE LINK System Hub: device list, RPMs, temperatures, fixed-% speeds.</summary>
    public sealed class LinkHub : IDisposable
    {
        public const ushort CorsairVendorId = 0x1b1c;
        public const ushort SystemHubProductId = 0x0c3f;

        private const int ResponseTimeoutMs = 500;

        private readonly IHidTransport _hid;
        private readonly CorsairGuard _guard;
        private readonly Action<string>? _debugLog;
        private bool _twoPageDeviceList = true;

        public string Firmware { get; private set; } = "?";

        public LinkHub(IHidTransport hid, CorsairGuard guard, Action<string>? debugLog = null)
        {
            _hid = hid;
            _guard = guard;
            _debugLog = debugLog;
        }

        public void Initialize()
        {
            // A leftover report from an interrupted exchange can be mistaken for the firmware answer,
            // so ask until two answers in a row agree.
            Firmware = Locked(() =>
            {
                string last = LinkProtocol.ParseFirmware(Send(LinkProtocol.CmdFirmware));
                for (int i = 0; i < 3; i++)
                {
                    string again = LinkProtocol.ParseFirmware(Send(LinkProtocol.CmdFirmware));
                    if (again == last) break;
                    last = again;
                }
                return last;
            });
            _twoPageDeviceList = LinkProtocol.FirmwareSupportsTwoPageDeviceList(Firmware);
            EnterSoftwareMode();
        }

        public void EnterSoftwareMode() => Locked(() => Send(LinkProtocol.CmdSoftwareMode));

        public void EnterHardwareMode() => Locked(() => Send(LinkProtocol.CmdHardwareMode));

        public List<LinkSubDevice> GetSubDevices()
        {
            return Locked(() =>
            {
                Send(LinkProtocol.CmdCloseEndpoint, LinkProtocol.EpSubDevices);
                Send(LinkProtocol.CmdOpenEndpoint, LinkProtocol.EpSubDevices);
                var page1 = Send(LinkProtocol.CmdRead, null, LinkProtocol.DtSubDevices);
                byte[]? page2 = _twoPageDeviceList ? Send(LinkProtocol.CmdRead) : null;
                Send(LinkProtocol.CmdCloseEndpoint, LinkProtocol.EpSubDevices);
                return LinkProtocol.ParseSubDevices(page1, page2);
            });
        }

        public List<LinkReading> GetSpeeds() =>
            LinkProtocol.ParseSpeeds(ReadEndpoint(LinkProtocol.EpSpeeds, LinkProtocol.DtSpeeds));

        public List<LinkReading> GetTemperatures() =>
            LinkProtocol.ParseTemperatures(ReadEndpoint(LinkProtocol.EpTemperatures, LinkProtocol.DtTemperatures));

        public void SetFixedPercent(IDictionary<int, byte> channelPercents)
        {
            if (channelPercents.Count == 0)
            {
                return;
            }
            var data = LinkProtocol.BuildFixedPercentSpeeds(channelPercents);
            WriteEndpoint(LinkProtocol.EpSetSpeeds, LinkProtocol.DtSetSpeeds, data);
        }

        private byte[] ReadEndpoint(byte[] endpoint, byte[] dataType)
        {
            return Locked(() =>
            {
                Send(LinkProtocol.CmdCloseEndpoint, endpoint);
                Send(LinkProtocol.CmdOpenEndpoint, endpoint);
                var res = Send(LinkProtocol.CmdRead, null, dataType);
                Send(LinkProtocol.CmdCloseEndpoint, endpoint);
                return res;
            });
        }

        private void WriteEndpoint(byte[] endpoint, byte[] dataType, byte[] data)
        {
            var body = LinkProtocol.BuildWriteData(dataType, data);
            Locked(() =>
            {
                Send(LinkProtocol.CmdCloseEndpoint, endpoint);
                Send(LinkProtocol.CmdOpenEndpoint, endpoint);
                Send(LinkProtocol.CmdWrite, body);
                Send(LinkProtocol.CmdCloseEndpoint, endpoint);
                return 0;
            });
        }

        private T Locked<T>(Func<T> work)
        {
            using (var lease = _guard.TryAcquire())
            {
                if (lease == null)
                {
                    throw new LinkBusyException();
                }
                return work();
            }
        }

        /// <summary>One request/response. Returns the response payload (report-id byte stripped).</summary>
        private byte[] Send(byte[] command, byte[]? data = null, byte[]? waitForDataType = null)
        {
            var report = LinkProtocol.BuildCommand(_hid.OutputReportLength, command, data);
            var raw = new byte[Math.Max(_hid.InputReportLength, 2)];

            _hid.FlushInput();
            _debugLog?.Invoke("TX " + LinkProtocol.Hex(report, 48));
            _hid.Write(report);

            if (!_hid.Read(raw, ResponseTimeoutMs))
            {
                throw new TimeoutException("Hub didn't answer in time.");
            }
            var payload = Strip(raw);
            _debugLog?.Invoke("RX " + LinkProtocol.Hex(payload, 48));

            var status = LinkProtocol.Status(payload);
            if (status != LinkProtocol.StatusOk)
            {
                throw new LinkCommandException($"Hub returned error 0x{status:x2} for command {LinkProtocol.Hex(command)}", status);
            }

            if (waitForDataType != null)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(ResponseTimeoutMs);
                while (!LinkProtocol.DataTypeIs(payload, waitForDataType))
                {
                    var left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0 || !_hid.Read(raw, left))
                    {
                        throw new TimeoutException($"Expected data type {LinkProtocol.Hex(waitForDataType)} never arrived.");
                    }
                    payload = Strip(raw);
                    _debugLog?.Invoke("RX " + LinkProtocol.Hex(payload, 48));
                }
            }

            return payload;
        }

        private static byte[] Strip(byte[] raw)
        {
            var p = new byte[raw.Length - 1];
            Buffer.BlockCopy(raw, 1, p, 0, p.Length);
            return p;
        }

        public void Dispose() => _hid.Dispose();
    }
}
