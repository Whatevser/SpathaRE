// Spatha L701 (original): finds the cable or dock and the collection that answers.

using HidSharp;

namespace SpathaRE.Core.SpathaL701;

/// <summary>Finds the collection of a connected Spatha that answers the protocol.</summary>
public static class L701Discovery
{
    public const int VendorId = 0x0B05;
    public const int PidWired = 0x181C;
    public const int PidDock = 0x1824;

    public static bool AnyPresent() =>
        DeviceList.Local.GetHidDevices(VendorId).Any(d => d.ProductID is PidWired or PidDock);

    /// <summary>Wired mouse first, then the dock; within a device, try every collection.</summary>
    public static (L701Protocol Protocol, bool Wired, string Path)? Find()
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
                var t = L701HidTransport.Open(dev.DevicePath, rid);
                if (t is null) continue;
                // IfSpatha only uses report id 0 on a collection whose feature report is 9 bytes.
                if (rid == 0x00 && t.FeatureLength != 9) { t.Dispose(); continue; }
                Logger.WriteLine($"Spatha: probing {dev.DevicePath} rid 0x{rid:X2}, feature length {t.FeatureLength}");
                var p = new L701Protocol(t);
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
