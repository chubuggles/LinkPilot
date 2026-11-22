using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LinkPilot.Hid
{
    /// <summary>What the protocol layer needs from a HID connection (lets tests use a fake device).</summary>
    public interface IHidTransport : IDisposable
    {
        int OutputReportLength { get; }
        int InputReportLength { get; }
        void Write(byte[] report);
        /// <summary>Reads one input report. Returns false on timeout.</summary>
        bool Read(byte[] buffer, int timeoutMs);
        void FlushInput();
    }

    public sealed class HidDeviceInfo
    {
        public string Path = "";
        public ushort VendorId;
        public ushort ProductId;
        public string Product = "";
        public string Serial = "";
        public int InputReportLength;
        public int OutputReportLength;
        public ushort UsagePage;

        public override string ToString() =>
            $"{VendorId:x4}:{ProductId:x4} '{Product}' serial={Serial} usagePage=0x{UsagePage:x4} in={InputReportLength} out={OutputReportLength}";
    }

    /// <summary>A HID device opened with overlapped I/O so reads and writes can time out.</summary>
    public sealed class HidDevice : IHidTransport
    {
        private readonly SafeFileHandle _handle;
        private readonly IntPtr _event;
        private readonly IntPtr _overlapped;
        private readonly IntPtr _buffer;
        private readonly int _bufferSize;
        private bool _disposed;

        public int OutputReportLength { get; }
        public int InputReportLength { get; }
        public HidDeviceInfo Info { get; }

        private HidDevice(SafeFileHandle handle, HidDeviceInfo info)
        {
            _handle = handle;
            Info = info;
            OutputReportLength = info.OutputReportLength;
            InputReportLength = info.InputReportLength;
            _bufferSize = Math.Max(OutputReportLength, InputReportLength);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
            _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OVERLAPPED)));
            _event = Native.CreateEvent(IntPtr.Zero, true, false, null);
        }

        public static HidDevice Open(HidDeviceInfo info)
        {
            var handle = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new IOException($"Could not open HID device ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
            }
            return new HidDevice(handle, info);
        }

        /// <summary>Lists HID interfaces for a vendor (and optionally a set of product IDs).</summary>
        public static List<HidDeviceInfo> Enumerate(ushort vendorId, ICollection<ushort>? productIds = null)
        {
            var result = new List<HidDeviceInfo>();
            Native.HidD_GetHidGuid(out var hidGuid);
            var set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                for (int index = 0; ; index++)
                {
                    var ifData = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA)) };
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref ifData))
                    {
                        break; // ERROR_NO_MORE_ITEMS
                    }

                    var path = GetInterfacePath(set, ref ifData);
                    if (path == null)
                    {
                        continue;
                    }

                    var info = Probe(path, vendorId, productIds);
                    if (info != null)
                    {
                        result.Add(info);
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }

            return result;
        }

        private static string? GetInterfacePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA ifData)
        {
            Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int required, IntPtr.Zero);
            if (required <= 0)
            {
                return null;
            }

            var detail = Marshal.AllocHGlobal(required);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on 64-bit, 6 on 32-bit.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, required, out _, IntPtr.Zero))
                {
                    return null;
                }
                return Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        private static HidDeviceInfo? Probe(string path, ushort vendorId, ICollection<ushort>? productIds)
        {
            // Zero access rights: lets us read attributes even while another app has the device open.
            using (var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid)
                {
                    return null;
                }

                var attrs = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES)) };
                if (!Native.HidD_GetAttributes(h, ref attrs) || attrs.VendorID != vendorId)
                {
                    return null;
                }
                if (productIds != null && productIds.Count > 0 && !productIds.Contains(attrs.ProductID))
                {
                    return null;
                }

                var info = new HidDeviceInfo
                {
                    Path = path,
                    VendorId = attrs.VendorID,
                    ProductId = attrs.ProductID,
                    Product = ReadHidString(h, Native.HidD_GetProductString),
                    Serial = ReadHidString(h, Native.HidD_GetSerialNumberString),
                };

                if (Native.HidD_GetPreparsedData(h, out var pp))
                {
                    try
                    {
                        if (Native.HidP_GetCaps(pp, out var caps) == Native.HIDP_STATUS_SUCCESS)
                        {
                            info.InputReportLength = caps.InputReportByteLength;
                            info.OutputReportLength = caps.OutputReportByteLength;
                            info.UsagePage = caps.UsagePage;
                        }
                    }
                    finally
                    {
                        Native.HidD_FreePreparsedData(pp);
                    }
                }

                return info;
            }
        }

        private delegate bool HidStringGetter(SafeFileHandle h, byte[] buffer, int len);

        private static string ReadHidString(SafeFileHandle h, HidStringGetter getter)
        {
            var buf = new byte[256];
            if (!getter(h, buf, buf.Length))
            {
                return "";
            }
            var s = Encoding.Unicode.GetString(buf);
            var nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        public void FlushInput()
        {
            Native.HidD_FlushQueue(_handle);
        }

        public void Write(byte[] report)
        {
            ThrowIfDisposed();
            if (report.Length != OutputReportLength)
            {
                throw new ArgumentException($"Output report must be {OutputReportLength} bytes (got {report.Length}).");
            }

            Marshal.Copy(report, 0, _buffer, report.Length);
            if (!DoOverlapped(write: true, report.Length, 1000, out _))
            {
                throw new TimeoutException("HID write timed out.");
            }
        }

        public bool Read(byte[] buffer, int timeoutMs)
        {
            ThrowIfDisposed();
            int len = Math.Min(buffer.Length, InputReportLength);
            if (!DoOverlapped(write: false, len, timeoutMs, out int transferred))
            {
                return false;
            }

            Array.Clear(buffer, 0, buffer.Length);
            Marshal.Copy(_buffer, buffer, 0, Math.Min(transferred, buffer.Length));
            return true;
        }

        private bool DoOverlapped(bool write, int length, int timeoutMs, out int transferred)
        {
            transferred = 0;
            Native.ResetEvent(_event);
            var ov = new Native.OVERLAPPED { hEvent = _event };
            Marshal.StructureToPtr(ov, _overlapped, false);

            bool ok = write
                ? Native.WriteFile(_handle, _buffer, length, IntPtr.Zero, _overlapped)
                : Native.ReadFile(_handle, _buffer, length, IntPtr.Zero, _overlapped);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_IO_PENDING)
                {
                    throw new IOException($"HID {(write ? "write" : "read")} failed ({new Win32Exception(err).Message})", err);
                }

                uint wait = Native.WaitForSingleObject(_event, (uint)timeoutMs);
                if (wait != Native.WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(_handle, _overlapped);
                    Native.GetOverlappedResult(_handle, _overlapped, out _, true); // let the cancel finish before reusing buffers
                    return false;
                }
            }

            if (!Native.GetOverlappedResult(_handle, _overlapped, out transferred, false))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"HID {(write ? "write" : "read")} failed ({new Win32Exception(err).Message})", err);
            }
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HidDevice));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try { Native.CancelIoEx(_handle, IntPtr.Zero); } catch { }
            _handle.Dispose();
            Native.CloseHandle(_event);
            Marshal.FreeHGlobal(_overlapped);
            Marshal.FreeHGlobal(_buffer);
        }
    }
}
