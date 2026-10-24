// App-wide: finds both kinds of mouse, keeps them polled, and runs every operation
// the UI asks for. The only place that knows about both models.

using System.Drawing;
using HidSharp;
using SpathaRE.Core.Shared;
using SpathaRE.Core.SpathaX;
using SpathaRE.Core.SpathaL701;

namespace SpathaRE.Core;

/// <summary>
/// Detection, hotplug, battery polling, onboard-profile tracking and every
/// device operation the UI needs. Devices are exposed as IMouseDevice: the
/// Spatha X through SpathaXDevice, the original Spatha through L701Device.
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
    private static readonly Dictionary<IMouseDevice, bool> _lastReady = new();
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
    /// <summary>Battery and charging state. One request per mouse, so it can run often; the
    /// original Spatha's dock also reports put-on / taken-off at once (see L701Device).</summary>
    private static readonly System.Timers.Timer _batteryPoll = new(10_000) { AutoReset = true };
    private static readonly System.Timers.Timer _profilePoll = new(1500) { AutoReset = true };
    /// <summary>A Spatha dock with the mouse switched off doesn't answer, and switching the
    /// mouse on fires no USB event. Retry quietly while that's the situation.</summary>
    private static readonly System.Timers.Timer _spathaRetry = new(6000) { AutoReset = false };

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
        // One-shot and re-armed after each scan: probing a dock whose mouse is asleep
        // takes a few seconds, and scans must not pile up behind each other.
        _spathaRetry.Elapsed += (_, _) => Task.Run(() =>
        {
            try
            {
                bool have;
                lock (_lock) have = _devices.OfType<L701Device>().Any();
                if (!have && L701Discovery.AnyPresent()) lock (_detectLock) DetectSpatha();
            }
            finally { if (_running) _spathaRetry.Start(); }
        });
    }

    public static List<IMouseDevice> Snapshot() { lock (_lock) return new List<IMouseDevice>(_devices); }


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


    private static volatile bool _running;

    public static void Start()
    {
        _running = true;
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
        _running = false;
        _batteryPoll.Stop();
        _profilePoll.Stop();
        _spathaRetry.Stop();
        foreach (var d in Snapshot()) Remove(d);
    }

    // ---- detection -------------------------------------------------------

    private static IEnumerable<AsusMouse> AsusModels()
    {
        yield return new SpathaXWireless();
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
                lock (_lock) known = _devices.OfType<SpathaXDevice>().Any(d => d.Mouse.Equals(candidate));
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

        var dev = new SpathaXDevice(mouse);
        mouse.Disconnect += (_, _) => Remove(dev);
        mouse.MouseReadyChanged += (_, _) => DevicesChanged?.Invoke(dev, EventArgs.Empty);
        mouse.BatteryUpdated += (_, _) => BatteryChanged?.Invoke(dev, EventArgs.Empty);
        mouse.ButtonBindingsChanged += (_, _) => BindingsChanged?.Invoke(dev, EventArgs.Empty);
        Add(dev);
    }

    private static void DetectSpatha()
    {
        bool have;
        lock (_lock) have = _devices.OfType<L701Device>().Any();
        if (have || !L701Discovery.AnyPresent()) return;

        var found = L701Discovery.Find();
        if (found is null) { Logger.WriteLine("Spatha present but no collection answered"); return; }

        var dev = new L701Device(found.Value.Protocol, found.Value.Wired, found.Value.Path);
        try { dev.Synchronize(); }
        catch (Exception e) { Logger.WriteLine("Spatha sync failed: " + e.Message); }

        // Anything the mouse or dock sends (profile, DPI, battery, put on / taken off
        // the dock) is also a sign of life, so a sleeping mouse comes back at once.
        dev.ExternalChange += (_, _) => Task.Run(() =>
        {
            NotifyIfReadyChanged(dev);
            PollProfile(dev);
            BatteryChanged?.Invoke(dev, EventArgs.Empty);
        });
        Add(dev);
    }

    /// <summary>The Spatha has no disconnect event of its own; drop it when its USB device is gone.</summary>
    private static void DropDisconnected()
    {
        foreach (var d in Snapshot().OfType<L701Device>())
            if (!L701Discovery.AnyPresent()) Remove(d);
    }

    private static void Add(IMouseDevice dev)
    {
        lock (_lock)
        {
            _devices.Add(dev);
            _lastProfile[dev] = dev.IsReady ? dev.Profile : -1;
            _lastReady[dev] = dev.IsReady;
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
            _lastReady.Remove(dev);
            _locks.Remove(dev);
            if (_selected == dev) _selected = null;
        }
        try { dev.Dispose(); } catch { }
        Logger.WriteLine($"{dev.Name} disconnected");
        DevicesChanged?.Invoke(dev, EventArgs.Empty);
    }

    // ---- polling ---------------------------------------------------------

    private static int _batteryRunning;

    /// <summary>Reads every mouse's battery now, e.g. when a window opens. Runs in the background.</summary>
    public static void RefreshBatteryNow() => Task.Run(RefreshBattery);

    public static void RefreshBattery()
    {
        // The timer and RefreshBatteryNow can overlap; one pass at a time is enough.
        if (Interlocked.Exchange(ref _batteryRunning, 1) == 1) return;
        try { RefreshBatteryPass(); }
        finally { Volatile.Write(ref _batteryRunning, 0); }
    }

    private static void RefreshBatteryPass()
    {
        foreach (var d in Snapshot())
        {
            if (!d.HasBattery) continue;
            var io = Lock(d);
            // Wait for the profile poll (it holds the lock for a few hundred ms over the
            // dock) instead of skipping: skipping made charging changes go unseen for minutes.
            if (!io.Wait(4000)) continue;
            try
            {
                if (!d.IsReady) d.PollWake(); else d.ReadBattery();
            }
            catch (Exception e) { Logger.WriteLine($"{d.Name}: battery read failed: {e.Message}"); }
            finally { io.Release(); }
            NotifyIfReadyChanged(d);
            BatteryChanged?.Invoke(d, EventArgs.Empty);
        }
    }

    private static void PollProfiles()
    {
        foreach (var d in Snapshot()) PollProfile(d);
    }

    /// <summary>Raises DevicesChanged when a mouse went to sleep or woke up since last time.</summary>
    private static void NotifyIfReadyChanged(IMouseDevice d)
    {
        bool changed;
        lock (_lock)
        {
            if (!_lastReady.TryGetValue(d, out bool was)) return;
            changed = was != d.IsReady;
            _lastReady[d] = d.IsReady;
        }
        if (!changed) return;
        Logger.WriteLine(d.IsReady ? $"{d.Name}: answering again" : $"{d.Name}: stopped answering (asleep or out of range)");
        DevicesChanged?.Invoke(d, EventArgs.Empty);
    }

    /// <summary>The original Spatha has a one-request probe, so a sleeping one is checked on
    /// every profile tick and shows up again within seconds of waking.</summary>
    private static void TryWake(IMouseDevice d)
    {
        var io = Lock(d);
        if (!io.Wait(0)) return;
        try { d.PollWake(); }
        catch (Exception e) { Logger.WriteLine($"{d.Name}: wake check failed: {e.Message}"); }
        finally { io.Release(); }
        if (d.IsReady) lock (_lock) _lastProfile[d] = d.Profile;
        NotifyIfReadyChanged(d);
    }

    private static void PollProfile(IMouseDevice d)
    {
        if (!d.IsReady)
        {
            if (d is L701Device) TryWake(d);
            return;
        }
        if (d.ProfileCount < 2) return;
        var io = Lock(d);
        if (!io.Wait(0)) return;
        int previous;
        lock (_lock) previous = _lastProfile.GetValueOrDefault(d, -1);
        bool changed;
        try { changed = d.PollProfileChanged(); }
        catch (Exception e) { Logger.WriteLine($"{d.Name}: profile poll failed: {e.Message}"); return; }
        finally { io.Release(); }

        NotifyIfReadyChanged(d);
        if (!d.IsReady) return;
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
        if (profile < 0 || profile >= d.ProfileCount) return;   // e.g. the profile 6 hotkey on a Spatha X
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

    // ---- export / import -------------------------------------------------

    /// <summary>Reads the given profiles (buttons and DPI). Switches back to the
    /// active profile afterwards if the mouse had to change profile to read them.</summary>
    public static Task<ProfileFile?> ExportAsync(IMouseDevice? d, IReadOnlyList<int> profiles) => Task.Run(() =>
    {
        if (d is null || !d.IsReady) return null;
        BusyChanged?.Invoke(d, true);
        var io = Lock(d);
        io.Wait();
        int original = d.Profile;
        try
        {
            var file = new ProfileFile { Model = d.Model.ToString() };
            foreach (int p in profiles) file.Profiles.Add(d.ExportProfile(p));
            return file;
        }
        catch (Exception e)
        {
            Logger.WriteLine($"{d.Name}: export failed: {e.Message}");
            WriteCompleted?.Invoke(d, "export failed: " + e.Message);
            return null;
        }
        finally
        {
            RestoreProfile(d, original);
            io.Release();
            BusyChanged?.Invoke(d, false);
        }
    });

    /// <summary>Writes each (target profile, data) pair. Throws nothing; problems end up in the report.</summary>
    public static Task<ImportReport> ImportAsync(IMouseDevice? d, IReadOnlyList<(int Target, PortableProfile Data)> items) => Task.Run(() =>
    {
        var report = new ImportReport();
        if (d is null || !d.IsReady) { report.Note("The mouse isn't connected."); return report; }
        BusyChanged?.Invoke(d, true);
        var io = Lock(d);
        io.Wait();
        int original = d.Profile;
        try
        {
            foreach (var (target, data) in items)
            {
                try { d.ImportProfile(target, data, report); }
                catch (Exception e)
                {
                    report.Note($"Profile {target + 1}: failed: {e.Message}");
                    Logger.WriteLine($"{d.Name}: import into profile {target + 1} failed: {e.Message}");
                }
            }
        }
        finally
        {
            RestoreProfile(d, original);
            io.Release();
            BusyChanged?.Invoke(d, false);
        }
        BindingsChanged?.Invoke(d, EventArgs.Empty);
        ProfileChanged?.Invoke(d, new ProfileChangedEventArgs(original, d.Profile, external: false));
        return report;
    });

    private static void RestoreProfile(IMouseDevice d, int original)
    {
        try
        {
            if (d.Profile != original) d.SetProfile(original);
            lock (_lock) _lastProfile[d] = d.Profile;
        }
        catch (Exception e) { Logger.WriteLine($"{d.Name}: couldn't switch back to profile {original + 1}: {e.Message}"); }
    }

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
