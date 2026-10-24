using System.Windows.Input;

namespace MouseHud.UI;

/// <summary>WPF keys to USB HID keyboard usages (page 0x07), for the shortcut recorder.</summary>
public static class HidKeys
{
    public static byte ModifierBit(Key k) => k switch
    {
        Key.LeftCtrl => 0x01, Key.LeftShift => 0x02, Key.LeftAlt => 0x04, Key.LWin => 0x08,
        Key.RightCtrl => 0x10, Key.RightShift => 0x20, Key.RightAlt => 0x40, Key.RWin => 0x80,
        _ => 0,
    };

    public static byte? Usage(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return (byte)(0x04 + (k - Key.A));
        if (k >= Key.D1 && k <= Key.D9) return (byte)(0x1E + (k - Key.D1));
        if (k >= Key.F1 && k <= Key.F12) return (byte)(0x3A + (k - Key.F1));
        if (k >= Key.F13 && k <= Key.F24) return (byte)(0x68 + (k - Key.F13));
        if (k >= Key.NumPad1 && k <= Key.NumPad9) return (byte)(0x59 + (k - Key.NumPad1));
        return k switch
        {
            Key.D0 => 0x27, Key.Enter => 0x28, Key.Escape => 0x29, Key.Back => 0x2A, Key.Tab => 0x2B,
            Key.Space => 0x2C, Key.OemMinus => 0x2D, Key.OemPlus => 0x2E, Key.OemOpenBrackets => 0x2F,
            Key.OemCloseBrackets => 0x30, Key.OemPipe => 0x31, Key.OemSemicolon => 0x33,
            Key.OemQuotes => 0x34, Key.OemTilde => 0x35, Key.OemComma => 0x36, Key.OemPeriod => 0x37,
            Key.OemQuestion => 0x38, Key.CapsLock => 0x39, Key.PrintScreen => 0x46, Key.Scroll => 0x47,
            Key.Pause => 0x48, Key.Insert => 0x49, Key.Home => 0x4A, Key.PageUp => 0x4B,
            Key.Delete => 0x4C, Key.End => 0x4D, Key.PageDown => 0x4E, Key.Right => 0x4F,
            Key.Left => 0x50, Key.Down => 0x51, Key.Up => 0x52, Key.NumLock => 0x53,
            Key.Divide => 0x54, Key.Multiply => 0x55, Key.Subtract => 0x56, Key.Add => 0x57,
            Key.NumPad0 => 0x62, Key.Decimal => 0x63, Key.Apps => 0x65,
            _ => null,
        };
    }
}
