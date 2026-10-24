using System.Drawing;
using HidSharp;
using MouseHud.Core.Devices;
using MouseHud.Core.Mouse;
using MouseHud.Core.Mouse.Models;
using MouseHud.Core.Spatha;

namespace MouseHud.Core;

/// <summary>
/// Detection, hotplug, battery polling, onboard-profile tracking and every
/// device operation the UI needs. Devices are exposed as IMouseDevice: the
/// Spatha X through AsusMouseDevice, the original Spatha through SpathaDevice.
/// All USB I/O runs off the UI thread and is serialised through <see cref="_io"/>.
/// </summary>
public static class MouseManager
{
    private static readonly object _lock = new();
    private static readonly List<IMouseDevice> _devices = new();
    private static readonly SemaphoreSlim _io = new(1, 1);

    private static readonly System.Timers.Timer _hotplugDebounce = new(1000) { AutoReset = false };
    private static readonly System.Timers.Timer _batteryPoll = new(30_000) { AutoReset = true };
    private static readonly System.Timers.Timer _profilePoll = new(1500) { AutoReset = true };
    /// <summary>A Spatha dock with the mouse switched off doesn't answer, and switching the
    /// mouse on fires no USB event. Retry quietly while that's the situation.</summary>
    private static readonly System.Timers.Timer _spathaRetry = new(8000) { AutoReset = true };

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
        _profilePoll.Elapsed += (_, _) => Task.Run(PollProfile);
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

    public static IMouseDevice? Active
    {
        get { lock (_lock) return _devices.FirstOrDefault(m => m.IsReady) ?? _devices.FirstOrDefault(); }
    }

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
        _io.Wait();
        try { dev.Synchronize(); }
        catch (Exception e) { Logger.WriteLine("Spatha sync failed: " + e.Message); }
        finally { _io.Release(); }

        dev.ExternalChange += (_, _) => Task.Run(PollProfile);
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
        lock (_lock) _devices.Add(dev);
        _lastKnownProfile = dev.IsReady ? dev.Profile : -1;
        Logger.WriteLine($"{dev.Name} ({dev.ConnectionLabel}) connected");
        DevicesChanged?.Invoke(dev, EventArgs.Empty);
    }

    private static void Remove(IMouseDevice dev)
    {
        lock (_lock) { if (!_devices.Remove(dev)) return; }
        try { dev.Dispose(); } catch { }
        _lastKnownProfile = -1;
        Logger.WriteLine($"{dev.Name} disconnected");
        DevicesChanged?.Invoke(dev, EventArgs.Empty);
    }

    // ---- polling ---------------------------------------------------------

    public static void RefreshBattery()
    {
        foreach (var d in Snapshot())
        {
            if (!d.HasBattery) continue;
            if (!_io.Wait(0)) return;      // never queue behind a user action
            bool wasReady = d.IsReady;
            try
            {
                if (!d.IsReady) d.Synchronize(); else d.ReadBattery();
            }
            catch (Exception e) { Logger.WriteLine($"{d.Name}: battery read failed: {e.Message}"); }
            finally { _io.Release(); }
            if (d.IsReady != wasReady) DevicesChanged?.Invoke(d, EventArgs.Empty);
            BatteryChanged?.Invoke(d, EventArgs.Empty);
        }
    }

    private static int _lastKnownProfile = -1;

    private static void PollProfile()
    {
        var d = Active;
        if (d is null || !d.IsReady || d.ProfileCount < 2) return;
        if (!_io.Wait(0)) return;
        int previous = _lastKnownProfile;
        bool changed;
        try { changed = d.PollProfileChanged(); }
        catch (Exception e) { Logger.WriteLine("Profile poll failed: " + e.Message); return; }
        finally { _io.Release(); }

        if (!d.IsReady)
        {
            Logger.WriteLine($"{d.Name}: stopped answering (asleep?)");
            DevicesChanged?.Invoke(d, EventArgs.Empty);
            return;
        }

        if (!changed && d.Profile == previous) return;
        _lastKnownProfile = d.Profile;
        Logger.WriteLine($"{d.Name}: profile changed on the mouse {previous + 1} -> {d.Profile + 1}");
        ProfileChanged?.Invoke(d, new ProfileChangedEventArgs(previous, d.Profile, external: true));
    }

    // ---- operations ------------------------------------------------------

    private static Task Run(string what, Action<IMouseDevice> action, bool busy = false) => Task.Run(() =>
    {
        var d = Active;
        if (d is null || !d.IsReady) return;
        if (busy) BusyChanged?.Invoke(d, true);
        _io.Wait();
        string? error = null;
        try { action(d); }
        catch (Exception e)
        {
            error = e.Message;
            Logger.WriteLine($"{what} failed: {e.Message}");
        }
        finally
        {
            _io.Release();
            if (busy) BusyChanged?.Invoke(d, false);
        }
        WriteCompleted?.Invoke(d, error);
    });

    public static Task SetProfileAsync(int profile) => Run($"SetProfile({profile})", d =>
    {
        int previous = d.Profile;
        d.SetProfile(profile);
        _lastKnownProfile = d.Profile;
        ProfileChanged?.Invoke(d, new ProfileChangedEventArgs(previous, d.Profile, external: false));
    }, busy: true);

    public static Task CycleProfileAsync(int direction = 1)
    {
        var d = Active;
        if (d is null || !d.IsReady || d.ProfileCount < 2) return Task.CompletedTask;
        int n = d.ProfileCount;
        return SetProfileAsync(((d.Profile + direction) % n + n) % n);
    }

    public static Task SetBindingAsync(int slot, ButtonAction action) =>
        Run($"SetBinding({slot})", d =>
        {
            d.SetBinding(slot, action);
            BindingsChanged?.Invoke(d, EventArgs.Empty);
        });

    public static Task ResetBindingsAsync() =>
        Run("ResetBindings", d =>
        {
            d.ResetBindings();
            BindingsChanged?.Invoke(d, EventArgs.Empty);
        }, busy: true);

    public static Task SetDpiAsync(int stage, int dpi, Color? color) =>
        Run($"SetDpi({stage})", d => d.SetDpi(stage, dpi, color));

    public static Task ApplySettingAsync(string id, int value) =>
        Run($"Setting {id}={value}", d => d.ApplySetting(id, value));

    public static Task SetLightingAsync(int zone, LightState state) =>
        Run($"SetLighting({zone})", d => d.SetLighting(zone, state));

    public static Task ResyncAsync() =>
        Run("Resync", d => { d.Synchronize(); _lastKnownProfile = d.Profile; }, busy: true);
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
