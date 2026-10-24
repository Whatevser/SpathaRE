using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace SpathaRE.Core.Spatha;

/// <summary>
/// Feature-report channel on one HID collection, opened the way Armoury does
/// it (CreateFile + HidD_Set/GetFeature). Falls back to a zero-access handle,
/// which Windows allows even on collections it holds exclusively.
/// </summary>
public sealed class WinHidTransport : ISpathaTransport
{
    private readonly SafeFileHandle _handle;
    private readonly int _featureLength;

    public byte ReportId { get; }
    public string Path { get; }

    private WinHidTransport(SafeFileHandle handle, int featureLength, byte reportId, string path)
    {
        _handle = handle;
        _featureLength = featureLength;
        ReportId = reportId;
        Path = path;
    }

    public int FeatureLength => _featureLength;

    public static WinHidTransport? Open(string path, byte reportId)
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
        return new WinHidTransport(h, len, reportId, path);
    }

    public bool SetFeature(byte[] report)
    {
        var buf = new byte[Math.Max(_featureLength, report.Length)];
        Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
        return Native.HidD_SetFeature(_handle, buf, buf.Length);
    }

    public byte[]? GetFeature()
    {
        var buf = new byte[_featureLength];
        buf[0] = ReportId;
        return Native.HidD_GetFeature(_handle, buf, buf.Length) ? buf : null;
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

/// <summary>Finds the collection of a connected Spatha that answers the protocol.</summary>
public static class SpathaDiscovery
{
    public const int VendorId = 0x0B05;
    public const int PidWired = 0x181C;
    public const int PidDock = 0x1824;

    public static bool AnyPresent() =>
        DeviceList.Local.GetHidDevices(VendorId).Any(d => d.ProductID is PidWired or PidDock);

    /// <summary>Wired mouse first, then the dock; within a device, try every collection.</summary>
    public static (SpathaProtocol Protocol, bool Wired, string Path)? Find()
    {
        var asus = DeviceList.Local.GetHidDevices(VendorId).ToList();
        Logger.WriteLine("Spatha: ASUS HID collections: " +
            string.Join(", ", asus.Select(d => $"{d.ProductID:X4}").Distinct()));
        var candidates = asus
            .Where(d => d.ProductID is PidWired or PidDock)
            .OrderBy(d => d.ProductID == PidWired ? 0 : 1)
            .ThenBy(d => d.DevicePath.Contains("col03", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        foreach (var dev in candidates)
        {
            foreach (byte rid in new byte[] { 0x0C, 0x00 })
            {
                var t = WinHidTransport.Open(dev.DevicePath, rid);
                if (t is null) continue;
                // IfSpatha only uses report id 0 on a collection whose feature report is 9 bytes.
                if (rid == 0x00 && t.FeatureLength != 9) { t.Dispose(); continue; }
                Logger.WriteLine($"Spatha: probing {dev.DevicePath} rid 0x{rid:X2}, feature length {t.FeatureLength}");
                var p = new SpathaProtocol(t);
                byte? reg = p.Probe();
                if (reg is byte r && (r >> 4) is >= 1 and <= 8)
                {
                    Logger.WriteLine($"Spatha: protocol on {dev.DevicePath} (rid 0x{rid:X2}), reg19=0x{r:X2}");
                    return (p, dev.ProductID == PidWired, dev.DevicePath);
                }
                Logger.WriteLine($"Spatha: no answer (reg19={(reg is null ? "none" : $"0x{reg:X2}")})");
                p.Dispose();
            }
        }
        Logger.WriteLine(candidates.Count == 0
            ? "Spatha: no 0B05:181C / 0B05:1824 device present"
            : "Spatha: present but no collection answered (mouse asleep or Armoury Crate busy?)");
        return null;
    }
}
