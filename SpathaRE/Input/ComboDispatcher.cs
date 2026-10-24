// Both mice: catches a combo's F13-F24 key and types the combination.

using System.Text.RegularExpressions;
using SpathaRE.Core;
using Keys = System.Windows.Forms.Keys;

namespace SpathaRE.Input;

/// <summary>
/// Makes combo buttons work. A combo button doesn't send Ctrl+C itself: on both
/// Spathas it sends a spare key, F13-F24 (see Combos.cs). This catches that key as
/// a global hotkey and types the real combination. Without it, a button set to
/// Copy would just send F13.
/// </summary>
public sealed class ComboDispatcher
{
    private const string HotkeyGroup = "mousecombo";

    private readonly HotkeyManager _hotkeys;
    private readonly Action<Action> _toUiThread;

    public ComboDispatcher(HotkeyManager hotkeys, Action<Action> toUiThread)
    {
        _hotkeys = hotkeys;
        _toUiThread = toUiThread;

        MouseManager.BindingsChanged += (_, _) => Rebuild();
        MouseManager.DevicesChanged += (_, _) => Rebuild();
        MouseManager.ProfileChanged += (_, _) => Rebuild();
    }

    /// <summary>
    /// Registers a carrier hotkey for every combo action currently bound on the
    /// device. Only the ones in use are claimed — grabbing all twelve would take
    /// F13-F24 away from other apps for no reason.
    /// </summary>
    public void Rebuild() => _toUiThread(() =>
    {
        _hotkeys.UnregisterGroup(HotkeyGroup);

        // Both mice use the same twelve carriers: the Spatha X binds code 0x0068.., the
        // original Spatha the HID key F13.., which is the same number.
        var codes = new HashSet<ushort>();
        foreach (var d in MouseManager.Snapshot())
            foreach (ushort code in d.BoundCombos)
                if (Combos.Get(code) is not null) codes.Add(code);

        foreach (ushort code in codes)
        {
            ushort captured = code;
            uint vk = (uint)(Keys.F13 + (code - Combos.First));
            bool ok = _hotkeys.RegisterRaw(0, vk, () => Dispatch(captured), HotkeyGroup);
            if (!ok)
                Logger.WriteLine($"Combo carrier {Combos.CarrierKey(code)} unavailable — " +
                                 "another app holds it, so that button will do nothing");
        }

        if (codes.Count > 0)
            Logger.WriteLine($"Combo dispatcher watching {codes.Count} carrier key(s)");
    });

    private void Dispatch(ushort code)
    {
        if (Combos.Get(code) is not { } combo) return;

        string command = combo.Command;
        var keys = Combos.ParseCommand(command);

        if (keys.Count == 0)
        {
            Logger.WriteLine($"Combo '{combo.Name}' has no usable key sequence ('{command}')");
            return;
        }

        int hold = Math.Clamp(AppConfig.Get("combo_hold_ms", 10), 0, 500);

        // Off the UI thread: PressCombo sleeps between down and up, and blocking
        // the dispatcher thread would freeze the tray and stall the HID poller.
        Task.Run(() =>
        {
            try { KeyboardSynth.PressCombo(keys, hold); }
            catch (Exception e) { Logger.WriteLine($"Combo '{combo.Name}' failed: {e.Message}"); }
        });
    }
}
