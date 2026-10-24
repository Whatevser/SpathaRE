// Spatha L701 (original): sends and receives the feature reports through Windows.

using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace SpathaRE.Core.SpathaL701;

/// <summary>
/// Feature-report channel on one HID collection, opened the way Armoury does
/// it (CreateFile + HidD_Set/GetFeature). Falls back to a zero-access handle,
/// which Windows allows even on collections it holds exclusively.
/// </summary>
public sealed class L701HidTransport : IL701Transport
{
    private readonly SafeFileHandle _handle;
    private readonly int _featureLength;

    public byte ReportId { get; }
    public string Path { get; }

    private L701HidTransport(SafeFileHandle handle, int featureLength, byte reportId, string path)
    {
        _handle = handle;
        _featureLength = featureLength;
        ReportId = reportId;
        Path = path;
    }

    public int FeatureLength => _featureLength;

    public static L701HidTransport? Open(string path, byte reportId)
    {
        var h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            h.Dispose();
            h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        }
        if (h.IsInvalid)
        {
            Logger.WriteLine($"Spatha: can't open {path} (error {Marshal.GetLastWin32Error()})");
            h.Dispose();
            return null;
        }

        int len = 0;
        if (Native.HidD_GetPreparsedData(h, out var pp))
        {
            try
            {
                if (Native.HidP_GetCaps(pp, out var caps) == Native.HIDP_STATUS_SUCCESS)
                    len = caps.FeatureReportByteLength;
            }
            finally { Native.HidD_FreePreparsedData(pp); }
        }
        if (len < 2)
        {
            Logger.WriteLine($"Spatha: {path} has no feature reports, skipping");
            h.Dispose();
            return null;
        }
        return new L701HidTransport(h, len, reportId, path);
    }

    public bool SetFeature(byte[] report)
    {
        var buf = new byte[Math.Max(_featureLength, report.Length)];
        Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
        return Native.HidD_SetFeature(_handle, buf, buf.Length);
    }

    // Replies are at most 33 bytes. Armoury reads exactly 0x21, and the dock (whose
    // collection declares a 4101-byte report) never answers a full-length read, so
    // ask for 33 first and only fall back to the declared length if that's refused.
    private const int ReplyLength = 0x21;
    private int _readLength = ReplyLength;

    public byte[]? GetFeature()
    {
        var buf = new byte[Math.Min(_readLength, _featureLength)];
        buf[0] = ReportId;
        if (Native.HidD_GetFeature(_handle, buf, buf.Length)) return buf;
        if (_readLength >= _featureLength) return null;

        var full = new byte[_featureLength];
        full[0] = ReportId;
        if (!Native.HidD_GetFeature(_handle, full, full.Length)) return null;
        Logger.WriteLine($"Spatha: short reads refused on {Path}, using {_featureLength} bytes");
        _readLength = _featureLength;
        return full;
    }

    public void Dispose() => _handle.Dispose();

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
    }
}
