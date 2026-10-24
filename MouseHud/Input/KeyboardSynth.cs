using System.Runtime.InteropServices;
using Keys = System.Windows.Forms.Keys;

namespace MouseHud.Input;

/// <summary>
/// Synthesises keystrokes for mouse combo actions.
///
/// Uses SendInput rather than the older keybd_event: SendInput delivers the whole
/// sequence as one atomic batch, so nothing can interleave between the modifier
/// going down and the key following it. It also lets us supply real scan codes,
/// which matters because games reading raw input or DirectInput often ignore
/// events that carry no scan code.
/// </summary>
public static class KeyboardSynth
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint INPUT_MOUSE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>
    /// Keys that live on the extended half of the keyboard. Blanket-setting the
    /// extended flag on every key (as some implementations do) makes Windows
    /// build a wrong lParam for ordinary keys, which some applications reject.
    /// </summary>
    private static readonly HashSet<ushort> ExtendedKeys = new()
    {
        0x03, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
        0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5,
    };

    /// <summary>
    /// Presses <paramref name="keys"/> in order, holds briefly, then releases in
    /// reverse. Reverse release is what makes modifier combos work: Ctrl must
    /// still be down when C goes up, or the target app sees a bare C.
    /// </summary>
    public static void PressCombo(IReadOnlyList<Keys> keys, int holdMs = 10)
    {
        if (keys.Count == 0) return;

        // Mouse buttons can't be batched with keyboard input meaningfully; a combo
        // of exactly one mouse button is the only sensible case.
        if (keys.Count == 1 && TryMouseButton(keys[0])) return;

        var batch = new List<INPUT>(keys.Count);
        foreach (var k in keys) batch.Add(KeyInput((ushort)k, down: true));
        Send(batch);

        if (holdMs > 0) Thread.Sleep(holdMs);

        batch.Clear();
        for (int i = keys.Count - 1; i >= 0; i--) batch.Add(KeyInput((ushort)keys[i], down: false));
        Send(batch);
    }

    private static bool TryMouseButton(Keys key)
    {
        uint down, up;
        switch (key)
        {
            case Keys.LButton: down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; break;
            case Keys.RButton: down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; break;
            case Keys.MButton: down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; break;
            default: return false;
        }

        Send(new List<INPUT>
        {
            new() { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = down } } },
            new() { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = up } } },
        });
        return true;
    }

    private static INPUT KeyInput(ushort vk, bool down)
    {
        ushort scan = (ushort)MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */);

        uint flags = KEYEVENTF_SCANCODE;
        if (ExtendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;

        // A few virtual keys have no scan code (media keys, browser keys). Fall
        // back to virtual-key injection for those rather than sending scan 0.
        if (scan == 0) flags = down ? 0 : KEYEVENTF_KEYUP;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = scan == 0 ? vk : (ushort)0,
                    wScan = scan,
                    dwFlags = flags,
                }
            }
        };
    }

    private static void Send(List<INPUT> inputs)
    {
        var arr = inputs.ToArray();
        uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length)
            Logger.WriteLine($"SendInput sent {sent}/{arr.Length} events (error {Marshal.GetLastWin32Error()})");
    }
}
