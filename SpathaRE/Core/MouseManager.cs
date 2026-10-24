using System.Drawing;
using HidSharp;
using SpathaRE.Core.Devices;
using SpathaRE.Core.Mouse;
using SpathaRE.Core.Mouse.Models;
using SpathaRE.Core.Spatha;

namespace SpathaRE.Core;

/// <summary>
/// Detection, hotplug, battery polling, onboard-profile tracking and every
/// device operation the UI needs. Devices are exposed as IMouseDevice: the
/// Spatha X through AsusMouseDevice, the original Spatha through SpathaDevice.
/// All USB I/O runs off the UI thread, serialised per device.
/// </summary>
public static class MouseManager
{
    private static readonly object _lock = new();
    private static readonly List<IMouseDevice> _devices = new();
    /// <summary>One lock per device: a multi-step operation on one mouse must not interleave
    /// with another on the same mouse, but two mice never wait for each other.</summary>
    private static readonly Dictionary<IMouseDevice, SemaphoreSlim> _locks = new();
    private static readonly Dictionary<IMouseDevice, int> _lastProfile = new();
    private static IMouseDevice? _selected;

    private static SemaphoreSlim Lock(IMouseDevice d)
    {
        lock (_lock)
        {
            if (!_locks.TryGetValue(d, out var l)) _locks[d] = l = new SemaphoreSlim(1, 1);
            return l;
        }
    }

    private static readonly System.Timers.Timer _hotplugDebounce = new(1000) { AutoReset = false };
    private static readonly System.Timers.Timer _batteryPoll = new(30_000) { AutoReset = true };
    private static readonly System.Timers.Timer _profilePoll = new(1500) { AutoReset = true };
    /// <summary>A Spatha dock with the mouse switched off doesn't answer, and switching the
    /// mouse on fires no USB event. Retry quietly while that's the situation.</summary>
    private static readonly System.Timers.Timer _spathaRetry = new(15000) { AutoReset = true };

    /// <summary>The device the UI and hotkeys act on changed.</summary>
    public static event EventHandler? SelectionChanged;
    /// <summary>A device appeared, disappeared, or became ready.</summary>
    public static event EventHandler? DevicesChanged;
    public static event EventHandler? BatteryChanged;
    public static event EventHandler? BindingsChanged;
    /// <summary>The active onboard profile changed (also when switched on the mouse itself).</summary>
    public static event EventHandler<ProfileChangedEventArgs>? ProfileChanged;
    /// <summary>A write finished; the argument is null on success or the error message.</summary>
    public static event EventHandler<string?>? WriteCompleted;
    /// <summary>True while a long operation (sync, profile switch) is running.</summary>
    public static event EventHandler<bool>? BusyChanged;

    static MouseManager()
    {
        _hotplugDebounce.Elapsed += (_, _) => Task.Run(DetectAll);
        _batteryPoll.Elapsed += (_, _) => Task.Run(RefreshBattery);
        _profilePoll.Elapsed += (_, _) => Task.Run(PollProfiles);
        _spathaRetry.Elapsed += (_, _) => Task.Run(() =>
        {
            bool have;
            lock (_lock) have = _devices.OfType<SpathaDevice>().Any();
            if (!have && SpathaDiscovery.AnyPresent()) lock (_detectLock) DetectSpatha();
        });
    }

    public static List<IMouseDevice> Snapshot() { lock (_lock) return new List<IMouseDevice>(_devices); }

    /// <summary>The G-Helper mice, for ComboDispatcher.</summary>
    public static List<AsusMouse> AsusSnapshot() =>
        Snapshot().OfType<AsusMouseDevice>().Select(d => d.Mouse).ToList();

    /// <summary>
    /// The mouse the settings window and hotkeys act on. Defaults to the one
    /// picked last time (remembered by name), then to the first ready one.
    /// </summary>
    public static IMouseDevice? Selected
    {
        get
        {
            lock (_lock)
            {
                if (_selected is not null && _devices.Contains(_selected)) return _selected;
                string? remembered = AppConfig.GetString("selected_device");
                return _devices.FirstOrDefault(d => d.Name == remembered && d.IsReady)
                    ?? _devices.FirstOrDefault(d => d.IsReady)
                    ?? _devices.FirstOrDefault();
            }
        }
        set
        {
            lock (_lock) _selected = value;
            if (value is not null) AppConfig.Set("selected_device", value.Name);
            SelectionChanged?.Invoke(value, EventArgs.Empty);
        }
    }

    /// <summary>Kept for older call sites: same as <see cref="Selected"/>.</summary>
    public static IMouseDevice? Active => Selected;

    public static void Start()
    {
        DeviceList.Local.Changed += (_, _) => { _hotplugDebounce.Stop(); _hotplugDebounce.Start(); };
        _batteryPoll.Start();
        _spathaRetry.Start();

        int interval = AppConfig.Get("profile_poll_ms", 1500);
        if (interval > 0)
        {
            _profilePoll.Interval = Math.Max(250, interval);
            _profilePoll.Start();
        }
        Task.Run(DetectAll);
    }

    public static void Stop()
    {
        _batteryPoll.Stop();
        _profilePoll.Stop();
        _spathaRetry.Stop();
        foreach (var d in Snapshot()) Remove(d);
    }

    // ---- detection -------------------------------------------------------

    private static IEnumerable<AsusMouse> AsusModels()
    {
        yield return new SpathaX();
        yield return new SpathaXWired();
    }

    private static readonly object _detectLock = new();

    public static void DetectAll()
    {
        lock (_detectLock)
        {
            DropDisconnected();
            DetectAsus();
            DetectSpatha();
        }
    }

    private static void DetectAsus()
    {
        foreach (var candidate in AsusModels())
        {
            try
            {
                if (!candidate.IsDeviceConnected()) { candidate.Dispose(); continue; }
                bool known;
                lock (_lock) known = _devices.OfType<AsusMouseDevice>().Any(d => d.Mouse.Equals(candidate));
                if (known) { candidate.Dispose(); continue; }
                ConnectAsus(candidate);
            }
            catch (Exception e) { Logger.WriteLine($"Detection failed for {candidate.GetDisplayName()}: {e.Message}"); }
        }
    }

    private static void ConnectAsus(AsusMouse mouse)
    {
        try { mouse.Connect(); }
        catch (IOException e) { Logger.WriteLine($"{mouse.GetDisplayName()} failed to open: {e.Message}"); return; }

        for (int i = 0; i < 3 && !mouse.IsDeviceReady; i++)
        {
            Thread.Sleep(250);
            try { mouse.SynchronizeDevice(); } catch (Exception e) { Logger.WriteLine("Sync error: " + e.Message); }
        }

        var dev = new AsusMouseDevice(mouse);
        mouse.Disconnect += (_, _) => Remove(dev);
        mouse.MouseReadyChanged += (_, _) => DevicesChanged?.Invoke(dev, EventArgs.Empty);
        mouse.BatteryUpdated += (_, _) => BatteryChanged?.Invoke(dev, EventArgs.Empty);
        mouse.ButtonBindingsChanged += (_, _) => BindingsChanged?.Invoke(dev, EventArgs.Empty);
        Add(dev);
    }

    private static void DetectSpatha()
    {
        bool have;
        lock (_lock) have = _devices.OfType<SpathaDevice>().Any();
        if (have || !SpathaDiscovery.AnyPresent()) return;

        var found = SpathaDiscovery.Find();
        if (found is null) { Logger.WriteLine("Spatha present but no collection answered"); return; }

        var dev = new SpathaDevice(found.Value.Protocol, found.Value.Wired, found.Value.Path);
        try { dev.Synchronize(); }
        catch (Exception e) { Logger.WriteLine("Spatha sync failed: " + e.Message); }

        dev.ExternalChange += (_, _) => Task.Run(() => PollProfile(dev));
        Add(dev);
    }

    /// <summary>The Spatha has no disconnect event of its own; drop it when its USB device is gone.</summary>
    private static void DropDisconnected()
    {
        foreach (var d in Snapshot().OfType<SpathaDevice>())
            if (!SpathaDiscovery.AnyPresent()) Remove(d);
    }

    private static void Add(IMouseDevice dev)
    {
        lock (_lock)
        {
            _devices.Add(dev);
            _lastProfile[dev] = dev.IsReady ? dev.Profile : -1;
        }
        Logger.WriteLine($"{dev.Name} ({dev.ConnectionLabel}) connected");
        DevicesChanged?.Invoke(dev, EventArgs.Empty);
    }

    private static void Remove(IMouseDevice dev)
    {
        lock (_lock)
        {
            if (!_devices.Remove(dev)) return;
            _lastProfile.Remove(dev);
            _locks.Remove(dev);
            if (_selected == dev) _selected = null;
        }
        try { dev.Dispose(); } catch { }
        Logger.WriteLine($"{dev.Name} disconnected");
        DevicesChanged?.Invoke(dev, EventArgs.Empty);
    }

    // ---- polling ---------------------------------------------------------

    public static void RefreshBattery()
    {
        foreach (var d in Snapshot())
        {
            if (!d.HasBattery) continue;
            var io = Lock(d);
            if (!io.Wait(0)) continue;     // never queue behind a user action
            bool wasReady = d.IsReady;
            try
            {
                if (!d.IsReady) d.Synchronize(); else d.ReadBattery();
            }
            catch (Exception e) { Logger.WriteLine($"{d.Name}: battery read failed: {e.Message}"); }
            finally { io.Release(); }
            if (d.IsReady != wasReady) DevicesChanged?.Invoke(d, EventArgs.Empty);
            BatteryChanged?.Invoke(d, EventArgs.Empty);
        }
    }

    private static void PollProfiles()
    {
        foreach (var d in Snapshot()) PollProfile(d);
    }

    private static void PollProfile(IMouseDevice d)
    {
        if (!d.IsReady || d.ProfileCount < 2) return;
        var io = Lock(d);
        if (!io.Wait(0)) return;
        int previous;
        lock (_lock) previous = _lastProfile.GetValueOrDefault(d, -1);
        bool changed;
        try { changed = d.PollProfileChanged(); }
        catch (Exception e) { Logger.WriteLine($"{d.Name}: profile poll failed: {e.Message}"); return; }
        finally { io.Release(); }

        if (!d.IsReady)
        {
            Logger.WriteLine($"{d.Name}: stopped answering (asleep?)");
            DevicesChanged?.Invoke(d, EventArgs.Empty);
            return;
        }
        if (!changed && d.Profile == previous) return;
        lock (_lock) _lastProfile[d] = d.Profile;
        Logger.WriteLine($"{d.Name}: profile changed on the mouse {previous + 1} -> {d.Profile + 1}");
        ProfileChanged?.Invoke(d, new ProfileChangedEventArgs(previous, d.Profile, external: true));
    }

    // ---- operations ------------------------------------------------------

    private static Task Run(IMouseDevice? d, string what, Action<IMouseDevice> action, bool busy = false) => Task.Run(() =>
    {
        if (d is null || !d.IsReady) return;
        if (busy) BusyChanged?.Invoke(d, true);
        var io = Lock(d);
        io.Wait();
        string? error = null;
        try { action(d); }
        catch (Exception e)
        {
            error = e.Message;
            Logger.WriteLine($"{d.Name}: {what} failed: {e.Message}");
        }
        finally
        {
            io.Release();
            if (busy) BusyChanged?.Invoke(d, false);
        }
        WriteCompleted?.Invoke(d, error);
    });

    public static Task SetProfileAsync(IMouseDevice? d, int profile) => Run(d, $"SetProfile({profile})", d =>
    {
        int previous = d.Profile;
        d.SetProfile(profile);
        lock (_lock) _lastProfile[d] = d.Profile;
        ProfileChanged?.Invoke(d, new ProfileChangedEventArgs(previous, d.Profile, external: false));
    }, busy: true);

    /// <summary>Hotkeys: act on the selected mouse.</summary>
    public static Task SetProfileAsync(int profile) => SetProfileAsync(Selected, profile);

    public static Task CycleProfileAsync(int direction = 1)
    {
        var d = Selected;
        if (d is null || !d.IsReady || d.ProfileCount < 2) return Task.CompletedTask;
        int n = d.ProfileCount;
        return SetProfileAsync(d, ((d.Profile + direction) % n + n) % n);
    }

    public static Task SetBindingAsync(IMouseDevice? d, int slot, ButtonAction action) =>
        Run(d, $"SetBinding({slot})", d =>
        {
            d.SetBinding(slot, action);
            BindingsChanged?.Invoke(d, EventArgs.Empty);
        });

    public static Task ResetBindingsAsync(IMouseDevice? d) =>
        Run(d, "ResetBindings", d =>
        {
            d.ResetBindings();
            BindingsChanged?.Invoke(d, EventArgs.Empty);
        }, busy: true);

    public static Task SetDpiAsync(IMouseDevice? d, int stage, int dpi, Color? color) =>
        Run(d, $"SetDpi({stage})", d => d.SetDpi(stage, dpi, color));

    public static Task ApplySettingAsync(IMouseDevice? d, string id, int value) =>
        Run(d, $"Setting {id}={value}", d => d.ApplySetting(id, value));

    public static Task SetLightingAsync(IMouseDevice? d, int zone, LightState state) =>
        Run(d, $"SetLighting({zone})", d => d.SetLighting(zone, state));

    public static Task ResyncAsync(IMouseDevice? d) =>
        Run(d, "Resync", d =>
        {
            d.Synchronize();
            lock (_lock) _lastProfile[d] = d.Profile;
        }, busy: true);
}

public sealed class ProfileChangedEventArgs : EventArgs
{
    public int PreviousProfile { get; }
    public int CurrentProfile { get; }
    /// <summary>True when the change came from the mouse itself.</summary>
    public bool External { get; }

    public ProfileChangedEventArgs(int previous, int current, bool external)
    {
        PreviousProfile = previous;
        CurrentProfile = current;
        External = external;
    }
}
