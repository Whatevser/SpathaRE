using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SpathaRE.Core;
using SpathaRE.Core.Devices;
using SpathaRE.Core.Mouse;
using SpathaRE.Input;
using DColor = System.Drawing.Color;
using Keys = System.Windows.Forms.Keys;
using MColor = System.Windows.Media.Color;

namespace SpathaRE.UI;

public partial class SettingsWindow : Window
{
    private IMouseDevice? _dev;
    private IMouseDevice? _mapDevice;
    private bool _loading;
    private string _page = "Buttons";
    private string _category = "";
    private List<ButtonAction> _visibleActions = new();
    private readonly Dictionary<string, DispatcherTimer> _debounce = new();
    private readonly DispatcherTimer _statusFade = new() { Interval = TimeSpan.FromSeconds(4) };

    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar();

        foreach (var nav in Nav.Children.OfType<RadioButton>())
            nav.Checked += (_, _) => ShowPage((string)nav.Tag);

        Map.SlotSelected += (_, slot) => ShowSlot(slot);
        ActionList.SelectionChanged += OnActionPicked;
        SearchBox.TextChanged += (_, _) => FillActionList();
        ResetBindings.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Reset every button of this profile to its factory function?", "SpathaRE",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await MouseManager.ResetBindingsAsync(_dev);
            Dispatcher.Invoke(RefreshButtons);
        };


        ComboRecorder.PreviewKeyDown += RecordComboKey;
        ComboRecorder.GotKeyboardFocus += (_, _) =>
        {
            if (_editingCombo == 0) return;
            _comboKeys.Clear();
            _comboTyping = false;
            ComboRecorder.IsReadOnly = true;
            ComboRecorder.Text = "Press the keys…";
        };
        ComboSave.Click += (_, _) => SaveCombo();
        ComboDefault.Click += (_, _) => ResetCombo();

        ExportButton.Click += (_, _) => ShowExportMenu();
        ImportButton.Click += async (_, _) => await Import();

        RescanButton.Click += (_, _) => { SetStatus("Searching for mice…"); Task.Run(MouseManager.DetectAll); };
        ResyncButton.Click += async (_, _) => { await MouseManager.ResyncAsync(_dev); Dispatcher.Invoke(RefreshAll); };

        _statusFade.Tick += (_, _) => { _statusFade.Stop(); StatusText.Text = "Ready"; StatusText.Foreground = Res("FgDim"); };

        MouseManager.DevicesChanged += OnDevices;
        MouseManager.SelectionChanged += OnSelection;
        MouseManager.ProfileChanged += OnProfile;
        MouseManager.BindingsChanged += OnBindings;
        MouseManager.BatteryChanged += OnBattery;
        MouseManager.WriteCompleted += OnWrite;
        MouseManager.BusyChanged += OnBusy;
        Closed += (_, _) =>
        {
            MouseManager.DevicesChanged -= OnDevices;
            MouseManager.SelectionChanged -= OnSelection;
            MouseManager.ProfileChanged -= OnProfile;
            MouseManager.BindingsChanged -= OnBindings;
            MouseManager.BatteryChanged -= OnBattery;
            MouseManager.WriteCompleted -= OnWrite;
            MouseManager.BusyChanged -= OnBusy;
        };

        BuildHotkeys();
        RefreshAll();
        // Like G-Helper: a fresh battery / charging reading whenever the window comes up.
        Activated += (_, _) => MouseManager.RefreshBatteryNow();
    }

    private void OnDevices(object? s, EventArgs e) => Dispatcher.BeginInvoke(RefreshAll);
    private void OnSelection(object? s, EventArgs e) => Dispatcher.BeginInvoke(RefreshAll);
    private void OnProfile(object? s, ProfileChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => { if (s == _dev) RefreshAll(); else RefreshDevices(); });
    private void OnBindings(object? s, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { if (s == _dev) RefreshButtons(); });
    private void OnBattery(object? s, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            RefreshDevices();
            if (s != _dev) return;
            RefreshHeader();
            if (_page == "Power" && _dev is { IsReady: true }) BuildPower();   // level and "charging" on the battery card
        });
    private void OnBusy(object? s, bool busy) => Dispatcher.BeginInvoke(() =>
        BusyText.Text = busy ? $"Talking to the {(s as IMouseDevice)?.Name ?? "mouse"}…" : "");
    private void OnWrite(object? s, string? error) => Dispatcher.BeginInvoke(() =>
    {
        if (s != _dev) return;
        if (error is null) SetStatus("Saved to the mouse", Res("Ok"));
        else SetStatus("Couldn't save: " + error, Res("Danger"));
    });

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    private static Style St(string key) => (Style)Application.Current.Resources[key];

    private void SetStatus(string text, Brush? color = null)
    {
        StatusText.Text = text;
        StatusText.Foreground = color ?? Res("FgDim");
        _statusFade.Stop();
        _statusFade.Start();
    }

    /// <summary>Coalesces rapid changes (slider drags) into one device write.</summary>
    private void Debounce(string key, Action action, int ms = 280)
    {
        _pending[key] = action;
        if (!_debounce.TryGetValue(key, out var t))
        {
            t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_pending.Remove(key, out var run)) run();
            };
            _debounce[key] = t;
        }
        t.Stop();
        t.Start();
    }

    private readonly Dictionary<string, Action> _pending = new();

    // =====================================================================
    // top level
    // =====================================================================

    private void ShowPage(string page)
    {
        _page = page;
        ButtonsPage.Visibility = page == "Buttons" ? Visibility.Visible : Visibility.Collapsed;
        PerformancePage.Visibility = page == "Performance" ? Visibility.Visible : Visibility.Collapsed;
        LightingPage.Visibility = page == "Lighting" ? Visibility.Visible : Visibility.Collapsed;
        PowerPage.Visibility = page == "Power" ? Visibility.Visible : Visibility.Collapsed;
        HotkeysPage.Visibility = page == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
    }

    /// <summary>The "no mouse" placeholder covers the mouse pages, never the app's own Hotkeys page.</summary>
    private void UpdateEmptyState()
    {
        bool none = _dev is null || !_dev.IsReady;
        EmptyState.Visibility = none && _page != "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        if (_dev is not null && !_dev.IsReady)
        {
            EmptyTitle.Text = $"{_dev.Name} is asleep or out of range";
            EmptyText.Text = "Move or click the mouse to wake it. It shows up again here within a few seconds.";
        }
        else
        {
            EmptyTitle.Text = "No supported mouse found";
            EmptyText.Text = "Plug in a ROG Spatha or Spatha X (cable or dock) and move it to wake it up. Close Armoury Crate so the two programs don't talk to the mouse at the same time.";
        }
        ExportButton.IsEnabled = ImportButton.IsEnabled = !none;
    }

    public void RefreshAll()
    {
        _dev = MouseManager.Selected;
        _loading = true;
        try
        {
            RefreshDevices();
            bool none = _dev is null || !_dev.IsReady;
            UpdateEmptyState();
            RefreshHeader();
            if (none) { Banner.Visibility = Visibility.Collapsed; return; }

            bool ro = _dev!.IsProfileReadOnly(_dev.Profile);
            Banner.Visibility = ro ? Visibility.Visible : Visibility.Collapsed;
            BannerText.Text = $"\"{_dev.ProfileName(_dev.Profile)}\" is the mouse's factory profile. Like Armoury Crate, SpathaRE keeps it read-only. Pick profile 1-5 to edit.";
            foreach (var page in new UIElement[] { ButtonsPage, PerformancePage, LightingPage, PowerPage })
                page.IsEnabled = !ro;
            Map.Opacity = ro ? 0.55 : 1;

            RefreshButtons();
            BuildPerformance();
            BuildLighting();
            BuildPower();
        }
        finally { _loading = false; }
    }

    /// <summary>The mouse list at the top of the rail; picking one switches the whole window to it.</summary>
    private void RefreshDevices()
    {
        DeviceList.Children.Clear();
        var all = MouseManager.Snapshot();
        DeviceListCaption.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var d in all)
        {
            string sub = !d.IsReady ? "asleep"
                : d.HasBattery && d.Battery >= 0 ? $"{d.ConnectionLabel} · {d.Battery}%{(d.Charging ? " ⚡" : "")}"
                : d.ConnectionLabel;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                Fill = d.IsReady ? Res("Ok") : Res("FgFaint"),
            };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Res("FgDim") });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(text);
            var rb = new RadioButton { Style = St("NavItem"), GroupName = "devices", Content = row, IsChecked = d == _dev, Margin = new Thickness(10, 1, 10, 1) };
            var captured = d;
            rb.Checked += (_, _) => { if (!_loading && captured != _dev) MouseManager.Selected = captured; };
            DeviceList.Children.Add(rb);
        }
    }

    private void RefreshHeader()
    {
        Chips.Children.Clear();
        ProfileStrip.Children.Clear();
        var d = _dev;
        if (d is null)
        {
            DeviceName.Text = "No mouse connected";
            return;
        }
        DeviceName.Text = d.Name;
        Chips.Children.Add(Chip(d.ConnectionLabel == "Wired" ? "" : "", d.ConnectionLabel, Res("FgDim")));
        if (!d.IsReady)
            Chips.Children.Add(Chip("", "Asleep or out of range", Res("Warn")));
        else if (d.HasBattery && d.Battery >= 0)
        {
            var color = d.Battery <= 15 ? Res("Danger") : d.Battery <= 35 ? Res("Warn") : Res("Ok");
            Chips.Children.Add(Chip(d.Charging ? "" : "", $"{d.Battery}%{(d.Charging ? " · charging" : "")}", color));
        }
        if (d.IsReady)
            Chips.Children.Add(Chip("", d.ProfileName(d.Profile), Res("FgDim")));

        if (!d.IsReady || d.ProfileCount < 2) return;
        for (int i = 0; i < d.ProfileCount; i++)
        {
            int p = i;
            string onMouse = Buttons.ProfileSwitchText(i);
            var rb = new RadioButton
            {
                Style = St("Pill"),
                GroupName = "profiles",
                MinWidth = 38,
                Content = d.IsProfileReadOnly(i) ? "\uE72E" : (i + 1).ToString(),
                FontFamily = d.IsProfileReadOnly(i) ? (FontFamily)Application.Current.Resources["IconFont"] : FontFamily,
                ToolTip = d.ProfileName(i) + (d.IsProfileReadOnly(i) ? " (read-only)" : "")
                          + (onMouse.Length > 0 ? $"\nOn the mouse: hold {onMouse}" : ""),
                IsChecked = d.Profile == i,
            };
            rb.Checked += async (_, _) => { if (!_loading) await MouseManager.SetProfileAsync(_dev, p); };
            // Show which buttons switch to this profile on the mouse itself.
            var slots = Buttons.ProfileSwitchSlots(d.Model, i);
            rb.MouseEnter += (_, _) => { if (_page == "Buttons" && _mapDevice == _dev) Map.Highlight(slots); };
            rb.MouseLeave += (_, _) => Map.Highlight(Array.Empty<int>());
            ProfileStrip.Children.Add(rb);
        }
    }

    private Border Chip(string glyph, string text, Brush color) => new()
    {
        Background = Res("SurfaceAlt"),
        BorderBrush = Res("Line"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(10, 3, 12, 4),
        Margin = new Thickness(0, 0, 8, 0),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], Foreground = color, Margin = new Thickness(0, 2, 7, 0), FontSize = 12 },
                new TextBlock { Text = text, Foreground = Res("Fg"), FontSize = 12 },
            },
        },
    };

    // =====================================================================
    // buttons
    // =====================================================================

    private void RefreshButtons()
    {
        var d = _dev;
        if (d is null || !d.IsReady) return;

        if (_mapDevice != d)
        {
            Map.Configure(d.Model, d.ButtonSlots);
            _mapDevice = d;
            BuildCategories(d);
            int first = d.ButtonSlots.Count > 0 ? d.ButtonSlots[0].Slot : -1;
            if (first >= 0) Map.Select(first);
        }
        Map.SetLabels(slot => d.GetBinding(slot)?.Label);
        if (Map.SelectedSlot >= 0) ShowSlot(Map.SelectedSlot);
    }

    private void BuildCategories(IMouseDevice d)
    {
        CategoryBar.Children.Clear();
        var names = d.ActionCatalog.Select(g => g.Name).ToList();
        _category = names.FirstOrDefault() ?? "";
        foreach (var name in names)
        {
            var rb = new RadioButton { Style = St("Pill"), GroupName = "cat", Content = name, IsChecked = name == _category, Padding = new Thickness(10, 5, 10, 5) };
            rb.Checked += (_, _) => { _category = name; FillActionList(); };
            CategoryBar.Children.Add(rb);
        }
    }

    private void ShowSlot(int slot)
    {
        var d = _dev;
        if (d is null) return;
        var def = d.ButtonSlots.FirstOrDefault(s => s.Slot == slot);
        if (def is null) return;
        SlotTitle.Text = def.Name;
        var current = d.GetBinding(slot);
        SlotCurrent.Text = current?.Label ?? "unknown";

        // Jump to the category that holds the current action.
        var group = current is null ? null : d.ActionCatalog.FirstOrDefault(g => g.Actions.Contains(current));
        string wanted = group?.Name ?? _category;
        foreach (var rb in CategoryBar.Children.OfType<RadioButton>())
            if ((string)rb.Content == wanted && rb.IsChecked != true) rb.IsChecked = true;
        _category = wanted;

        FillActionList();
    }

    private void FillActionList()
    {
        var d = _dev;
        if (d is null) return;
        ShowComboEditor();
        string q = SearchBox.Text.Trim();
        var group = d.ActionCatalog.FirstOrDefault(g => g.Name == _category);
        _visibleActions = (group?.Actions ?? Array.Empty<ButtonAction>())
            .Where(a => q.Length == 0 || a.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _loading = true;
        ActionList.ItemsSource = _visibleActions.Select(a => a.Label).ToList();
        var current = Map.SelectedSlot >= 0 ? d.GetBinding(Map.SelectedSlot) : null;
        ActionList.SelectedIndex = current is null ? -1 : _visibleActions.IndexOf(current);
        if (ActionList.SelectedIndex >= 0) ActionList.ScrollIntoView(ActionList.SelectedItem);
        _loading = false;
    }

    private async void OnActionPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Map.SelectedSlot < 0) return;
        int i = ActionList.SelectedIndex;
        if (i < 0 || i >= _visibleActions.Count) return;
        var action = _visibleActions[i];
        int slot = Map.SelectedSlot;
        await MouseManager.SetBindingAsync(_dev, slot, action);
        Dispatcher.Invoke(() =>
        {
            Map.SetLabels(s => _dev?.GetBinding(s)?.Label);
            SlotCurrent.Text = _dev?.GetBinding(slot)?.Label ?? action.Label;
            ShowComboEditor();
        });
    }

    // ---- combos (carrier key + replay), the same on both mice ----

    private ushort _editingCombo;
    private readonly List<Keys> _comboKeys = new();

    private static readonly Keys[] ModifierOrder =
        { Keys.LControlKey, Keys.RControlKey, Keys.LShiftKey, Keys.RShiftKey, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin };

    private static List<Keys> Ordered(IEnumerable<Keys> keys) =>
        keys.OrderBy(k => Array.IndexOf(ModifierOrder, k) is int i && i >= 0 ? i : 100).ToList();

    /// <summary>Combo carrier (0x68-0x73) of a binding on either mouse, or 0.</summary>
    private static ushort CarrierOf(ButtonAction? a) => a?.Payload switch
    {
        ushort u when AsusMouseDevice.IsCarrier(u) => u,
        byte[] b => Core.Spatha.SpathaDevice.CarrierOf(b),
        _ => 0,
    };

    /// <summary>The editor shows when the button has a combo or the Combos tab is open.</summary>
    private void ShowComboEditor()
    {
        var d = _dev;
        var current = d is not null && Map.SelectedSlot >= 0 ? d.GetBinding(Map.SelectedSlot) : null;
        ushort code = CarrierOf(current);
        bool isCombo = code != 0 && AsusMouse.CombosByCode.ContainsKey(code);
        bool comboTab = _category == "Combos";
        if (!isCombo && !comboTab)
        {
            ComboPanel.Visibility = Visibility.Collapsed;
            _editingCombo = 0;
            return;
        }
        ComboPanel.Visibility = Visibility.Visible;
        ComboStatus.Text = "";
        if (!isCombo)
        {
            _editingCombo = 0;
            ComboTitle.Text = "EDIT COMBO";
            ComboHelp.Text = "Pick one of the 12 combos above for this button. Each one makes the mouse send a spare key (F13-F24) and SpathaRE types the combination; you can change which keys it types right here.";
            ComboRecorder.Text = "";
            ComboRecorder.IsEnabled = ComboSave.IsEnabled = ComboDefault.IsEnabled = false;
            return;
        }

        _editingCombo = code;
        var combo = AsusMouse.CombosByCode[_editingCombo];
        string carrier = AsusMouseDevice.CarrierKey(_editingCombo);
        ComboTitle.Text = $"EDIT COMBO {carrier}";
        ComboHelp.Text = $"This button makes the mouse send {carrier}, and SpathaRE types the keys below. " +
                         "1. Click the box.  2. Press the new keys together.  3. Save keys. " +
                         $"Every button set to {carrier}, on either mouse and in any profile, changes with it. " +
                         "Keys Windows keeps for itself (Alt+Tab, Win+L) can't be pressed here: click the box, press Backspace and type them instead, e.g. Win+Shift+S.";
        ComboRecorder.IsEnabled = ComboSave.IsEnabled = ComboDefault.IsEnabled = true;
        _comboKeys.Clear();
        _comboTyping = false;
        ComboRecorder.IsReadOnly = true;
        ComboRecorder.Text = ComboDispatcher.Describe(combo.ResolveCommand());
        ComboStatus.Text = AppConfig.GetString(combo.ConfigKey) is null ? "Default keys." : "Custom keys.";
    }

    /// <summary>True after Backspace in an empty recorder: the box takes typed text like "Win+Shift+S".</summary>
    private bool _comboTyping;

    private void RecordComboKey(object sender, KeyEventArgs e)
    {
        if (_editingCombo == 0 || _comboTyping)
        {
            if (_comboTyping && e.Key == Key.Enter) { SaveCombo(); e.Handled = true; }
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return;
        if (key == Key.Back && _comboKeys.Count == 0)
        {
            _comboTyping = true;
            ComboRecorder.IsReadOnly = false;
            ComboRecorder.Text = "";
            ComboStatus.Text = "Type the keys joined with +, then Save keys (or Enter).";
            return;
        }
        var k = (Keys)KeyInterop.VirtualKeyFromKey(key);
        if (!_comboKeys.Contains(k) && _comboKeys.Count < 6) _comboKeys.Add(k);
        ComboRecorder.Text = ComboDispatcher.Describe(Ordered(_comboKeys));
    }

    private void SaveCombo()
    {
        if (_editingCombo == 0 || !AsusMouse.CombosByCode.TryGetValue(_editingCombo, out var combo)) return;
        var keys = _comboTyping ? ComboDispatcher.ParseCommand(ComboRecorder.Text.Trim()) : Ordered(_comboKeys);
        if (keys.Count == 0)
        {
            ComboStatus.Text = _comboTyping
                ? "Couldn't read that. Use names like Ctrl, Shift, Alt, Win, A, F5, Tab, joined with +."
                : "No keys yet: click the box and press them.";
            return;
        }
        AppConfig.Set(combo.ConfigKey, ComboDispatcher.ToCommand(keys));
        string saved = $"Saved. {AsusMouseDevice.CarrierKey(_editingCombo)} now types {ComboDispatcher.Describe(keys)}.";
        AfterComboChange();
        ComboStatus.Text = saved;
    }

    private void ResetCombo()
    {
        if (_editingCombo == 0 || !AsusMouse.CombosByCode.TryGetValue(_editingCombo, out var combo)) return;
        AppConfig.Remove(combo.ConfigKey);
        string done = $"Back to the default, {ComboDispatcher.Describe(combo.DefaultCommand)}.";
        AfterComboChange();
        ComboStatus.Text = done;
    }

    /// <summary>Combo labels show their keys, so everything that lists them is refreshed.</summary>
    private void AfterComboChange()
    {
        Map.SetLabels(s => _dev?.GetBinding(s)?.Label);
        if (Map.SelectedSlot >= 0) SlotCurrent.Text = _dev?.GetBinding(Map.SelectedSlot)?.Label ?? "—";
        FillActionList();
    }

    // =====================================================================
    // export / import
    // =====================================================================

    private void ShowExportMenu()
    {
        var d = _dev;
        if (d is null || !d.IsReady) return;
        var menu = new ContextMenu { PlacementTarget = ExportButton, Placement = PlacementMode.Bottom };
        var one = new MenuItem { Header = $"This profile ({d.ProfileName(d.Profile)})…" };
        one.Click += async (_, _) => await Export(new[] { d.Profile });
        var all = new MenuItem { Header = $"All {d.ProfileCount} profiles…" };
        all.Click += async (_, _) => await Export(Enumerable.Range(0, d.ProfileCount).ToList());
        menu.Items.Add(one);
        menu.Items.Add(all);
        menu.IsOpen = true;
    }

    private async Task Export(IReadOnlyList<int> profiles)
    {
        var d = _dev;
        if (d is null) return;
        string what = profiles.Count == 1 ? d.ProfileName(profiles[0]) : "all profiles";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export buttons and DPI",
            Filter = "SpathaRE profiles (*.json)|*.json",
            FileName = $"{d.Name} - {what}.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        SetStatus(profiles.Count > 1 ? "Reading every profile from the mouse…" : "Reading the profile…");
        var file = await MouseManager.ExportAsync(d, profiles);
        if (file is null) { SetStatus("Export failed, see the log", Res("Danger")); return; }
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, file.ToJson());
            SetStatus($"Exported {what} to {System.IO.Path.GetFileName(dlg.FileName)}", Res("Ok"));
        }
        catch (Exception e) { SetStatus("Couldn't write the file: " + e.Message, Res("Danger")); }
        RefreshAll();
    }

    private async Task Import()
    {
        var d = _dev;
        if (d is null || !d.IsReady) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import buttons and DPI",
            Filter = "SpathaRE profiles (*.json)|*.json|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        ProfileFile file;
        try { file = ProfileFile.Parse(System.IO.File.ReadAllText(dlg.FileName)); }
        catch (Exception e)
        {
            MessageBox.Show(this, "That file can't be imported: " + e.Message, "SpathaRE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string from = file.Model == nameof(DeviceModel.SpathaX) ? "Spatha X"
                    : file.Model == nameof(DeviceModel.Spatha) ? "original Spatha" : file.Model;
        bool cross = file.Model != d.Model.ToString();
        string note = "Only button functions and DPI are imported; lighting and the other settings stay as they are." +
                      (cross ? $"\n\nThe file comes from a {from}. Buttons are matched by position; anything this mouse can't do is left unchanged and listed afterwards." : "");

        List<(int, PortableProfile)> items;
        string question;
        if (file.Profiles.Count == 1)
        {
            if (d.IsProfileReadOnly(d.Profile))
            {
                MessageBox.Show(this, "The active profile is read-only. Switch to profile 1-5 first.", "SpathaRE", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var p = file.Profiles[0];
            items = new() { (d.Profile, p) };
            question = $"Import \"{p.Name}\" into {d.ProfileName(d.Profile)}?";
        }
        else
        {
            items = file.Profiles.Select(p => (p.Index, p)).ToList();
            question = $"Import {file.Profiles.Count} profiles into the same profile numbers on this mouse? " +
                       "Profiles this mouse doesn't have, and read-only ones, are skipped.";
        }
        if (MessageBox.Show(this, question + "\n\n" + note, "SpathaRE", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        SetStatus("Writing to the mouse…");
        var report = await MouseManager.ImportAsync(d, items);
        RefreshAll();
        string summary = $"Imported into {report.Written} profile(s).";
        if (report.Notes.Count > 0) summary += "\n\n• " + string.Join("\n• ", report.Notes);
        SetStatus($"Imported into {report.Written} profile(s)", Res(report.Written > 0 ? "Ok" : "Warn"));
        MessageBox.Show(this, summary, "SpathaRE", MessageBoxButton.OK,
            report.Notes.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.None);
    }

    // =====================================================================
    // hotkeys (app settings, not stored on a mouse)
    // =====================================================================

    private readonly Dictionary<string, TextBlock> _hotkeyStatus = new();

    private void BuildHotkeys()
    {
        HotkeysPanel.Children.Clear();
        _hotkeyStatus.Clear();

        var body = new StackPanel();
        body.Children.Add(CardTitle("Profile hotkeys",
            "Global keyboard shortcuts that switch the selected mouse's onboard profile. Click a box and press the new keys; Clear turns one off."));
        foreach (var (key, label, def) in App.HotkeyDefs)
            body.Children.Add(HotkeyRow(key, label, def));
        HotkeysPanel.Children.Add(new Border { Style = St("Card"), Child = body });

        var onMouse = new StackPanel();
        onMouse.Children.Add(CardTitle("On the mouse",
            "Both Spathas also switch profile by themselves: hold the DPI button and press a side button. Hover a profile number on the Buttons page to see those buttons in red."));
        for (int i = 0; i < 6; i++)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = i == 5 ? "Profile 6 (original Spatha)" : $"Profile {i + 1}", FontWeight = FontWeights.SemiBold });
            var t = new TextBlock { Text = Buttons.ProfileSwitchText(i), Foreground = Res("FgDim") };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            onMouse.Children.Add(g);
        }
        HotkeysPanel.Children.Add(new Border { Style = St("Card"), Child = onMouse });

        var general = new StackPanel();
        general.Children.Add(CardTitle("General", null));
        general.Children.Add(ConfigToggle("notify_profile_change", true, "Notify on profile change",
            "A tray notification when the profile is switched on the mouse."));
        general.Children.Add(ConfigToggle("usb_packet_logger", false, "Log every USB packet",
            "Spatha X only, for troubleshooting. Makes the log grow quickly."));
        var folder = new Button { Content = "Open log and config folder", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        folder.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Logger.AppPath);
        general.Children.Add(folder);
        HotkeysPanel.Children.Add(new Border { Style = St("Card"), Child = general });

        UpdateHotkeyStatus(null);
    }

    private UIElement ConfigToggle(string key, bool def, string label, string hint)
    {
        var sw = new CheckBox { Style = St("Switch"), IsChecked = AppConfig.GetBool(key, def), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) => AppConfig.Set(key, sw.IsChecked == true);
        return LabeledRow(label, hint, sw);
    }

    private UIElement LabeledRow(string label, string? hint, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        l.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        if (hint is not null) l.Children.Add(new TextBlock { Text = hint, Style = St("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(l);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private UIElement HotkeyRow(string key, string label, string def)
    {
        var box = new TextBox { IsReadOnly = true, Width = 220, Cursor = Cursors.Hand, FontFamily = new FontFamily("Consolas"), FontSize = 13 };
        string Shown() => App.HotkeyGesture(key) is { Length: > 0 } g ? Pretty(g) : "Off";
        box.Text = Shown();
        // While recording, the app's own hotkeys are released so pressing one reaches the box.
        box.GotKeyboardFocus += (_, _) => { App.Instance.SuspendHotkeys(); box.Text = "Press the keys…"; };
        box.LostKeyboardFocus += (_, _) => { box.Text = Shown(); UpdateHotkeyStatus(App.Instance.RegisterHotkeys()); };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            string mods = ModifierText(Keyboard.Modifiers);
            if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                box.Text = mods + "…";
                return;
            }
            if (k == Key.Escape && mods.Length == 0) { Keyboard.ClearFocus(); return; }
            string gesture = mods + (k >= Key.D0 && k <= Key.D9 ? ((char)('0' + (k - Key.D0))).ToString() : k.ToString());
            AppConfig.Set(key, gesture);
            Keyboard.ClearFocus();
        };

        var clear = new Button { Content = "Clear", Style = St("GhostButton"), Margin = new Thickness(8, 0, 0, 0) };
        clear.Click += (_, _) => { AppConfig.Set(key, ""); box.Text = Shown(); UpdateHotkeyStatus(App.Instance.RegisterHotkeys()); };
        var reset = new Button { Content = "Default", Style = St("GhostButton"), Margin = new Thickness(2, 0, 0, 0), ToolTip = Pretty(def) };
        reset.Click += (_, _) => { AppConfig.Remove(key); box.Text = Shown(); UpdateHotkeyStatus(App.Instance.RegisterHotkeys()); };
        var status = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        _hotkeyStatus[key] = status;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(clear);
        row.Children.Add(reset);
        row.Children.Add(status);
        return LabeledRow(label, null, row);
    }

    private void UpdateHotkeyStatus(Dictionary<string, bool>? results)
    {
        results ??= App.Instance.LastHotkeyResults;
        foreach (var (key, text) in _hotkeyStatus)
        {
            bool off = App.HotkeyGesture(key).Length == 0;
            bool ok = results.TryGetValue(key, out bool r) && r;
            text.Text = off ? "" : ok ? "active" : "taken by another app";
            text.Foreground = ok ? Res("Ok") : Res("Danger");
        }
    }

    private static string ModifierText(ModifierKeys m) =>
        ((m & ModifierKeys.Control) != 0 ? "Ctrl+" : "") + ((m & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
        ((m & ModifierKeys.Shift) != 0 ? "Shift+" : "") + ((m & ModifierKeys.Windows) != 0 ? "Win+" : "");

    /// <summary>"Ctrl+Alt+OemPlus" -> "Ctrl+Alt+=" for display.</summary>
    private static string Pretty(string gesture) => gesture
        .Replace("OemPlus", "=").Replace("OemMinus", "-").Replace("OemTilde", "`").Replace("OemComma", ",")
        .Replace("OemPeriod", ".").Replace("NumPad", "Num ");

    // =====================================================================
    // performance
    // =====================================================================

    private void BuildPerformance()
    {
        PerformancePanel.Children.Clear();
        var d = _dev!;
        PerformancePanel.Children.Add(BuildDpiCard(d));
        foreach (var section in d.Settings.Where(s => s.Page == "Performance"))
            PerformancePanel.Children.Add(BuildSection(section));
    }

    private Border BuildDpiCard(IMouseDevice d)
    {
        var dpi = d.Dpi;
        var body = new StackPanel();
        body.Children.Add(CardTitle("DPI", dpi.Stages.Length > 1
            ? "Each stage is a sensitivity the DPI button cycles through."
            : null));

        for (int i = 0; i < dpi.Stages.Length; i++)
        {
            int stage = i;
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = $"Stage {i + 1}", FontWeight = FontWeights.SemiBold });
            if (dpi.ActiveStage == i)
                label.Children.Add(new Border
                {
                    Background = Res("AccentSoft"), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock { Text = "active", FontSize = 10, Foreground = Res("Accent") },
                });
            grid.Children.Add(label);

            var slider = new Slider
            {
                Minimum = dpi.Min, Maximum = dpi.Max, SmallChange = dpi.Step, LargeChange = dpi.Step * 4,
                TickFrequency = dpi.Step, IsSnapToTickEnabled = true,
                Value = Math.Clamp(dpi.Stages[i], dpi.Min, dpi.Max), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            var box = new TextBox { Text = dpi.Stages[i].ToString(), Margin = new Thickness(12, 0, 0, 0), TextAlignment = TextAlignment.Right };
            Grid.SetColumn(box, 2);
            grid.Children.Add(box);

            Border? swatch = null;
            if (dpi.Colors is not null)
            {
                var c = dpi.Colors[i];
                swatch = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(12, 0, 0, 0),
                    Background = new SolidColorBrush(MColor.FromRgb(c.R, c.G, c.B)),
                    BorderBrush = Res("LineHi"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                    ToolTip = "Stage colour",
                };
                Grid.SetColumn(swatch, 3);
                grid.Children.Add(swatch);
            }

            DColor? color = dpi.Colors?[i];
            void Push()
            {
                int value = (int)slider.Value;
                Debounce($"dpi{stage}", () => _ = MouseManager.SetDpiAsync(_dev, stage, value, color));
            }
            slider.ValueChanged += (_, _) =>
            {
                if (_loading) return;
                box.Text = ((int)slider.Value).ToString();
                Push();
            };
            void CommitBox()
            {
                if (int.TryParse(box.Text, out int v))
                {
                    v = (int)Math.Round(Math.Clamp(v, dpi.Min, dpi.Max) / (double)dpi.Step) * dpi.Step;
                    slider.Value = v;
                    box.Text = v.ToString();
                }
                else box.Text = ((int)slider.Value).ToString();
            }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitBox(); };
            box.LostFocus += (_, _) => CommitBox();

            body.Children.Add(grid);

            if (swatch is not null)
            {
                var picker = new ColorPicker { Visibility = Visibility.Collapsed, Margin = new Thickness(110, 4, 0, 8) };
                picker.SetColor(color!.Value, notify: false);
                picker.ColorChanged += (_, c) =>
                {
                    color = c;
                    swatch.Background = new SolidColorBrush(MColor.FromRgb(c.R, c.G, c.B));
                    Push();
                };
                swatch.MouseLeftButtonDown += (_, _) =>
                    picker.Visibility = picker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                body.Children.Add(picker);
            }
        }
        return new Border { Style = St("Card"), Child = body };
    }

    // =====================================================================
    // generic settings sections
    // =====================================================================

    private Border BuildSection(SettingSection section)
    {
        var body = new StackPanel();
        body.Children.Add(CardTitle(section.Title, null));
        foreach (var item in section.Items)
            body.Children.Add(BuildItem(item));
        return new Border { Style = St("Card"), Child = body };
    }

    private UIElement BuildItem(SettingItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        label.Children.Add(new TextBlock { Text = item.Label, FontWeight = FontWeights.SemiBold });
        if (item.Hint is not null)
            label.Children.Add(new TextBlock { Text = item.Hint, Style = St("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(label);

        FrameworkElement control = item switch
        {
            ChoiceItem c => Choice(c),
            ToggleItem t => Toggle(t),
            RangeItem r => Range(r),
            _ => new TextBlock(),
        };
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private FrameworkElement Choice(ChoiceItem c)
    {
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        string group = "choice_" + c.Id;
        for (int i = 0; i < c.Options.Count; i++)
        {
            int idx = i;
            var rb = new RadioButton { Style = St("Pill"), GroupName = group, Content = c.Options[i], IsChecked = c.Selected == i };
            rb.Checked += (_, _) => { if (!_loading) _ = MouseManager.ApplySettingAsync(_dev, c.Id, idx); };
            wrap.Children.Add(rb);
        }
        return wrap;
    }

    private FrameworkElement Toggle(ToggleItem t)
    {
        var sw = new CheckBox { Style = St("Switch"), IsChecked = t.Value, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) => { if (!_loading) _ = MouseManager.ApplySettingAsync(_dev, t.Id, sw.IsChecked == true ? 1 : 0); };
        return sw;
    }

    private FrameworkElement Range(RangeItem r)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Width = 420 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        var slider = new Slider { Minimum = r.Min, Maximum = r.Max, TickFrequency = Math.Max(1, r.Step), IsSnapToTickEnabled = true, Value = r.Value };
        var text = new TextBlock { Text = $"{r.Value}{r.Unit}", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(text, 1);
        grid.Children.Add(slider);
        grid.Children.Add(text);
        slider.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            int v = (int)slider.Value;
            text.Text = $"{v}{r.Unit}";
            Debounce("range_" + r.Id, () => _ = MouseManager.ApplySettingAsync(_dev, r.Id, v));
        };
        return grid;
    }

    private StackPanel CardTitle(string title, string? hint)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = St("Caption") });
        if (hint is not null) sp.Children.Add(new TextBlock { Text = hint, Style = St("Muted"), Margin = new Thickness(0, 3, 0, 0) });
        return sp;
    }

    // =====================================================================
    // lighting
    // =====================================================================

    private void BuildLighting()
    {
        LightingPanel.Children.Clear();
        var zones = _dev!.LightZones;
        if (zones.Count == 0)
        {
            LightingPanel.Children.Add(new Border { Style = St("Card"), Child = new TextBlock { Text = "This mouse has no adjustable lighting.", Style = St("Muted") } });
            return;
        }
        for (int i = 0; i < zones.Count; i++)
            if (zones[i].Effects.Count > 0)
                LightingPanel.Children.Add(BuildZoneCard(i, zones[i], zones[i].State.Clone()));
    }

    private Border BuildZoneCard(int index, LightZone zone, LightState state)
    {
        var card = new Border { Style = St("Card") };
        var body = new StackPanel();
        card.Child = body;
        body.Children.Add(CardTitle(zone.Name, null));

        void Push() => Debounce("light" + index, () => _ = MouseManager.SetLightingAsync(_dev, index, state.Clone()));
        void Rebuild()
        {
            int pos = LightingPanel.Children.IndexOf(card);
            if (pos < 0) return;
            LightingPanel.Children.RemoveAt(pos);
            LightingPanel.Children.Insert(pos, BuildZoneCard(index, zone, state));
        }

        // effect
        var effects = new WrapPanel();
        for (int i = 0; i < zone.Effects.Count; i++)
        {
            int e = i;
            var rb = new RadioButton { Style = St("Pill"), GroupName = $"fx{index}", Content = zone.Effects[i].Name, IsChecked = state.Effect == i };
            rb.Checked += (_, _) =>
            {
                if (_loading) return;
                state.Effect = e;
                Push();
                Dispatcher.BeginInvoke(Rebuild);
            };
            effects.Children.Add(rb);
        }
        body.Children.Add(Row("Effect", effects));

        var fx = zone.Effects[Math.Clamp(state.Effect, 0, zone.Effects.Count - 1)];

        // colours
        if (fx.ColorCount > 0 && !state.Random)
        {
            if (state.Colors.Length < fx.ColorCount)
            {
                var grown = new DColor[fx.ColorCount];
                for (int i = 0; i < grown.Length; i++) grown[i] = i < state.Colors.Length ? state.Colors[i] : state.Colors.LastOrDefault();
                state.Colors = grown;
            }
            int editing = 0;
            var swatches = new WrapPanel();
            var picker = new ColorPicker { Margin = new Thickness(0, 8, 0, 0) };
            picker.SetColor(state.Colors[0], notify: false);
            var borders = new List<Border>();
            for (int i = 0; i < fx.ColorCount; i++)
            {
                int ci = i;
                var c = state.Colors[i];
                var b = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6),
                    Background = new SolidColorBrush(MColor.FromRgb(c.R, c.G, c.B)),
                    BorderThickness = new Thickness(2), BorderBrush = i == 0 ? Res("Accent") : Res("LineHi"),
                    Cursor = Cursors.Hand, ToolTip = $"Colour {i + 1}",
                };
                b.MouseLeftButtonDown += (_, _) =>
                {
                    editing = ci;
                    foreach (var x in borders) x.BorderBrush = Res("LineHi");
                    b.BorderBrush = Res("Accent");
                    picker.SetColor(state.Colors[ci], notify: false);
                };
                borders.Add(b);
                swatches.Children.Add(b);
            }
            picker.ColorChanged += (_, c) =>
            {
                state.Colors[editing] = c;
                borders[editing].Background = new SolidColorBrush(MColor.FromRgb(c.R, c.G, c.B));
                Push();
            };
            var colorBox = new StackPanel();
            colorBox.Children.Add(swatches);
            colorBox.Children.Add(picker);
            body.Children.Add(Row(fx.ColorCount > 1 ? "Colours" : "Colour", colorBox));
        }

        // brightness
        body.Children.Add(Row(zone.HasWirelessBrightness ? "Brightness (wired)" : "Brightness",
            LevelSlider(zone.MaxBrightness, state.Brightness, v => { state.Brightness = v; Push(); })));
        if (zone.HasWirelessBrightness)
            body.Children.Add(Row("Brightness (wireless)",
                LevelSlider(zone.MaxBrightness, state.WirelessBrightness, v => { state.WirelessBrightness = v; Push(); })));

        if (fx.HasSpeed)
        {
            var speed = new WrapPanel();
            string[] names = { "Slow", "Medium", "Fast" };
            for (int i = 0; i < 3; i++)
            {
                int s = i;
                var rb = new RadioButton { Style = St("Pill"), GroupName = $"speed{index}", Content = names[i], IsChecked = state.Speed == i };
                rb.Checked += (_, _) => { if (!_loading) { state.Speed = s; Push(); } };
                speed.Children.Add(rb);
            }
            body.Children.Add(Row("Speed", speed));
        }

        if (fx.HasRandom)
        {
            var sw = new CheckBox { Style = St("Switch"), IsChecked = state.Random, HorizontalAlignment = HorizontalAlignment.Left };
            sw.Click += (_, _) => { state.Random = sw.IsChecked == true; Push(); Dispatcher.BeginInvoke(Rebuild); };
            body.Children.Add(Row("Random colour", sw));
        }
        return card;
    }

    private FrameworkElement LevelSlider(int max, int value, Action<int> changed)
    {
        var grid = new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        var slider = new Slider { Minimum = 0, Maximum = max, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Clamp(value, 0, max) };
        string Text(int v) => v == 0 ? "Off" : $"{v * 100 / max}%";     // both mice: Off, 25, 50, 75, 100 %
        var text = new TextBlock { Text = Text(value), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(text, 1);
        grid.Children.Add(slider);
        grid.Children.Add(text);
        slider.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            int v = (int)slider.Value;
            text.Text = Text(v);
            changed(v);
        };
        return grid;
    }

    private static UIElement Row(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    // =====================================================================
    // power
    // =====================================================================

    private void BuildPower()
    {
        PowerPanel.Children.Clear();
        var d = _dev!;
        if (d.HasBattery)
        {
            var body = new StackPanel();
            body.Children.Add(CardTitle("Battery", null));
            int pct = Math.Max(0, d.Battery);
            var color = d.Battery < 0 ? Res("FgFaint") : pct <= 15 ? Res("Danger") : pct <= 35 ? Res("Warn") : Res("Ok");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            row.Children.Add(new TextBlock { Text = d.Battery < 0 ? "—" : $"{pct}%", FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = color });
            row.Children.Add(new TextBlock { Text = d.Charging ? "charging" : d.Battery < 0 ? "no reading yet" : "", Style = St("Muted"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 8) });
            body.Children.Add(row);
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 4), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Width = 520 };
            bar.Children.Add(new Border { Background = Res("SurfaceHi"), CornerRadius = new CornerRadius(3) });
            bar.Children.Add(new Border { Background = color, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 520 * pct / 100.0 });
            body.Children.Add(bar);
            PowerPanel.Children.Add(new Border { Style = St("Card"), Child = body });
        }
        var sections = d.Settings.Where(s => s.Page == "Power").ToList();
        foreach (var s in sections) PowerPanel.Children.Add(BuildSection(s));
        if (!d.HasBattery && sections.Count == 0)
            PowerPanel.Children.Add(new Border { Style = St("Card"), Child = new TextBlock { Text = "No power settings for this connection.", Style = St("Muted") } });
    }

    // =====================================================================
    // window chrome
    // =====================================================================

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void DarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));        // pre-20H1 value
            int caption = 0x000D0B07;                                        // COLORREF of Bg (#070B0D)
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));       // DWMWA_CAPTION_COLOR (Win11)
        }
        catch { }
    }
}
