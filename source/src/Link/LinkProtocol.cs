using System;
using System.Collections.Generic;
using System.Text;

namespace LinkPilot.Link
{
    public sealed class LinkSubDevice
    {
        public int Channel;
        public byte Model;
        public byte Variant;
        public string Id = "";
    }

    public struct LinkReading
    {
        public int Channel;
        public bool Available;
        public double Value;
    }

    /// <summary>
    /// Byte-level iCUE LINK System Hub protocol: packet builders and response parsers.
    /// Pure functions, so they can be unit tested without hardware.
    ///
    /// Command packet (output report, 513 bytes): [0]=report id 0, [1]=0, [2]=1, [3..]=command, then data.
    /// Response payload ("p" = input report minus the report-id byte):
    ///   p[3] = status (0 = ok, 3 = wrong mode), p[4..5] = data type, then data.
    /// </summary>
    public static class LinkProtocol
    {
        public const byte HandleId = 0x01;

        public static readonly byte[] CmdSoftwareMode = { 0x01, 0x03, 0x00, 0x02 };
        public static readonly byte[] CmdHardwareMode = { 0x01, 0x03, 0x00, 0x01 };
        public static readonly byte[] CmdFirmware = { 0x02, 0x13 };
        public static readonly byte[] CmdOpenEndpoint = { 0x0d, HandleId };
        public static readonly byte[] CmdCloseEndpoint = { 0x05, 0x01, HandleId };
        public static readonly byte[] CmdRead = { 0x08, HandleId };
        public static readonly byte[] CmdWrite = { 0x06, HandleId };

        public static readonly byte[] EpSpeeds = { 0x17 };
        public static readonly byte[] EpSetSpeeds = { 0x18 };
        public static readonly byte[] EpTemperatures = { 0x21 };
        public static readonly byte[] EpSubDevices = { 0x36 };

        public static readonly byte[] DtSpeeds = { 0x25, 0x00 };
        public static readonly byte[] DtTemperatures = { 0x10, 0x00 };
        public static readonly byte[] DtSetSpeeds = { 0x07, 0x00 };
        public static readonly byte[] DtSubDevices = { 0x21, 0x00 };

        public const byte StatusOk = 0x00;
        public const byte StatusWrongMode = 0x03;

        public static byte[] BuildCommand(int reportLength, byte[] command, byte[]? data = null)
        {
            var buf = new byte[reportLength];
            buf[2] = 0x01;
            Buffer.BlockCopy(command, 0, buf, 3, command.Length);
            if (data != null && data.Length > 0)
            {
                Buffer.BlockCopy(data, 0, buf, 3 + command.Length, data.Length);
            }
            return buf;
        }

        /// <summary>Wraps endpoint data: [len LE16 = data+2][00 00][data type][data].</summary>
        public static byte[] BuildWriteData(byte[] dataType, byte[] data)
        {
            var buf = new byte[4 + dataType.Length + data.Length];
            int len = data.Length + 2;
            buf[0] = (byte)(len & 0xff);
            buf[1] = (byte)(len >> 8);
            Buffer.BlockCopy(dataType, 0, buf, 4, dataType.Length);
            Buffer.BlockCopy(data, 0, buf, 4 + dataType.Length, data.Length);
            return buf;
        }

        /// <summary>Speed table: [count] then per channel [channel][0x00 = fixed %][percent][0x00].</summary>
        public static byte[] BuildFixedPercentSpeeds(IDictionary<int, byte> channelPercents)
        {
            var keys = new List<int>(channelPercents.Keys);
            keys.Sort();
            var data = new byte[keys.Count * 4 + 1];
            data[0] = (byte)keys.Count;
            int i = 1;
            foreach (var ch in keys)
            {
                data[i] = (byte)ch;
                data[i + 1] = 0x00;
                data[i + 2] = Math.Min((byte)100, channelPercents[ch]);
                i += 4;
            }
            return data;
        }

        public static byte Status(byte[] payload) => payload.Length > 3 ? payload[3] : (byte)0xff;

        public static bool DataTypeIs(byte[] payload, byte[] dataType) =>
            payload.Length > 5 && payload[4] == dataType[0] && payload[5] == dataType[1];

        public static string ParseFirmware(byte[] payload)
        {
            int build = payload[6] | (payload[7] << 8);
            return $"{payload[4]}.{payload[5]}.{build}";
        }

        public static bool FirmwareSupportsTwoPageDeviceList(string fw)
        {
            var parts = fw.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            {
                return true;
            }
            return major >= 3 || (major == 2 && minor >= 5);
        }

        /// <summary>
        /// Parses the device list. Data starts at payload[6] on the first page and payload[4] on the
        /// continuation page. Layout: [last channel], then per channel an 8-byte header
        /// ([2]=model, [3]=variant, [7]=id length; 0 length = empty port) followed by the ASCII id.
        /// Channels are numbered from 1.
        /// </summary>
        public static List<LinkSubDevice> ParseSubDevices(byte[] page1, byte[]? page2 = null)
        {
            var joined = new List<byte>();
            if (page1.Length > 6)
            {
                for (int k = 6; k < page1.Length; k++) joined.Add(page1[k]);
            }
            if (page2 != null && page2.Length > 4)
            {
                for (int k = 4; k < page2.Length; k++) joined.Add(page2[k]);
            }

            var result = new List<LinkSubDevice>();
            if (joined.Count == 0)
            {
                return result;
            }

            var p = joined.ToArray();
            int lastChannel = p[0];
            int i = 1;

            for (int ch = 1; ch <= lastChannel; ch++)
            {
                if (i + 8 > p.Length)
                {
                    break;
                }

                int idLen = p[i + 7];
                if (idLen == 0)
                {
                    i += 8;
                    continue;
                }

                byte model = p[i + 2];
                byte variant = p[i + 3];
                int idStart = i + 8;
                bool runsPastEnd = idStart + idLen > p.Length;
                int take = Math.Max(0, Math.Min(idLen, p.Length - idStart));

                var sb = new StringBuilder();
                for (int k = 0; k < take; k++)
                {
                    byte b = p[idStart + k];
                    if (b == 0) break;
                    sb.Append((char)b);
                }
                var id = sb.ToString();

                result.Add(new LinkSubDevice { Channel = ch, Model = model, Variant = variant, Id = id });

                if (runsPastEnd)
                {
                    break;
                }

                // Advance by the id length actually present (firmware quirk: some ids are shorter than reported).
                i += 8 + id.Length;
            }

            return result;
        }

        /// <summary>Speeds: payload[6]=count, then 3 bytes each: [status 0=ok][rpm LE16]. Index = channel.</summary>
        public static List<LinkReading> ParseSpeeds(byte[] payload) => ParseTriples(payload, 1.0);

        /// <summary>Temperatures: same layout as speeds, value is tenths of a degree C.</summary>
        public static List<LinkReading> ParseTemperatures(byte[] payload) => ParseTriples(payload, 10.0);

        private static List<LinkReading> ParseTriples(byte[] payload, double divisor)
        {
            var list = new List<LinkReading>();
            if (payload.Length < 7)
            {
                return list;
            }

            int count = payload[6];
            for (int i = 0; i < count; i++)
            {
                int o = 7 + i * 3;
                if (o + 2 >= payload.Length)
                {
                    break;
                }
                bool ok = payload[o] == 0x00;
                short raw = (short)(payload[o + 1] | (payload[o + 2] << 8));
                list.Add(new LinkReading { Channel = i, Available = ok, Value = ok ? raw / divisor : 0 });
            }
            return list;
        }

        public static string Hex(byte[] data, int max = 64)
        {
            var sb = new StringBuilder();
            int n = Math.Min(max, data.Length);
            for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2"));
            if (data.Length > n) sb.Append("…");
            return sb.ToString();
        }
    }
}
