using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LinkPilot.Hid;
using LinkPilot.Link;

namespace LinkPilot.Core
{
    public sealed class ChannelState
    {
        public int Channel;
        public string DeviceId = "";
        public LinkModelInfo Model = LinkModels.Lookup(0);
        public byte Variant;
        public int? Rpm;
        public double? Temp;
        public bool Controllable;
        public int AppliedPercent;
        public string DriverNote = "";
        public string DisplayName = "";
    }

    public sealed class HubSnapshot
    {
        public bool Connected;
        public string Status = "Starting…";
        public string Firmware = "";
        public List<ChannelState> Channels = new List<ChannelState>();
        public double? CoolantTemp;
        public string CoolantSource = "";
        public int? PumpRpm;
        public bool FullBlast;
        public string? Alarm;
        public string LockInfo = "";
    }

    public sealed class Notification
    {
        public string Title = "";
        public string Text = "";
        public bool IsWarning;
    }

    /// <summary>
    /// Background control loop: reads the hub, runs fan/pump curves with safety overrides, writes speeds.
    /// The UI only ever reads <see cref="Latest"/> and edits settings through <see cref="Edit"/>.
    /// </summary>
    public sealed class Controller : IDisposable
    {
        private const int RampUpPerTick = 15;
        private const int RampDownPerTick = 4;
        private const int RewriteEveryMs = 10000;
        private const int RefreshDeviceListEveryMs = 30000;
        private const int StallTicksToAlarm = 3;
        private const int StallGraceMs = 8000;
        private const int StatusEveryMs = 2000;
        private const int HistoryEveryMs = 60000;

        private readonly AppSettings _settings;
        private readonly object _settingsLock = new object();
        private readonly CorsairGuard _guard = new CorsairGuard();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread? _thread;

        private LinkHub? _hub;
        private List<ChannelState> _channels = new List<ChannelState>();
        private readonly Dictionary<int, double> _applied = new Dictionary<int, double>();
        private Dictionary<int, byte> _lastWritten = new Dictionary<int, byte>();
        private DateTime _lastWrite = DateTime.MinValue;
        private DateTime _lastDeviceList = DateTime.MinValue;
        private DateTime _connectedAt = DateTime.MinValue;
        private int _consecutiveErrors;
        private readonly Dictionary<int, int> _stallTicks = new Dictionary<int, int>();
        private bool _tempAlarm;
        private bool _stallAlarm;
        private volatile bool _fullBlast;
        private DateTime _lastStatusWrite = DateTime.MinValue;
        private DateTime _lastHistoryWrite = DateTime.MinValue;
        private bool _lastStatusConnected;
        private bool _statusErrorLogged;

        private volatile HubSnapshot _latest = new HubSnapshot();
        public HubSnapshot Latest { get => _latest; private set => _latest = value; }
        public ConcurrentQueue<Notification> Notifications { get; } = new ConcurrentQueue<Notification>();

        private readonly Func<IHidTransport>? _transportFactory;

        /// <param name="transportFactory">Tests pass a simulated hub here; the app leaves it null and uses USB.</param>
        public Controller(AppSettings settings, Func<IHidTransport>? transportFactory = null)
        {
            _settings = settings;
            _transportFactory = transportFactory;
        }

        /// <summary>True when talking to a pretend hub (tests and --demo) instead of USB.</summary>
        public bool IsSimulated => _transportFactory != null;

        public bool FullBlast
        {
            get => _fullBlast;
            set { _fullBlast = value; _wake.Set(); }
        }

        private volatile bool _dirty;

        /// <summary>Thread-safe settings edit; nudges the loop, which saves to disk (debounced).</summary>
        public void Edit(Action<AppSettings> change)
        {
            lock (_settingsLock)
            {
                change(_settings);
                _settings.Sanitize();
            }
            _dirty = true;
            _wake.Set();
        }

        private void SaveIfDirty()
        {
            if (!_dirty) return;
            _dirty = false;
            lock (_settingsLock) _settings.Save();
        }

        public T Read<T>(Func<AppSettings, T> read)
        {
            lock (_settingsLock) return read(_settings);
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "LinkPilot control loop" };
            _thread.Start();
        }

        // ------------------------------------------------------------------ loop

        private void Run()
        {
            Log.Write("Control loop started. " + _guard.Description);
            while (!_stop.WaitOne(0))
            {
                int interval = Read(s => s.PollIntervalMs);
                SaveIfDirty();
                PublishFiles();
                try
                {
                    if (_hub == null)
                    {
                        if (!TryConnect())
                        {
                            WaitOrStop(3000);
                            continue;
                        }
                    }
                    Tick();
                    _consecutiveErrors = 0;
                }
                catch (LinkBusyException)
                {
                    // SignalRGB (or another tool) had the lock; just try again.
                }
                catch (LinkCommandException ex) when (ex.StatusCode == LinkProtocol.StatusWrongMode)
                {
                    Log.Write("Hub left software mode. This is normal when SignalRGB starts or closes. Taking it back.");
                    SafeCall(() => _hub?.EnterSoftwareMode());
                    _lastWritten.Clear();
                }
                catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is ObjectDisposedException || ex is IndexOutOfRangeException || ex is ArgumentException)
                {
                    _consecutiveErrors++;
                    Log.Write($"Hub communication problem ({_consecutiveErrors}): {ex.Message}");
                    if (_consecutiveErrors >= 3)
                    {
                        Disconnect("Lost contact with the hub — reconnecting…");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Unexpected error: " + ex);
                    Disconnect("Unexpected error — reconnecting…");
                }

                WaitOrStop(interval);
            }
        }

        /// <summary>
        /// Keeps status.json (the live state) and history.csv (one line a minute) up to date,
        /// so scripts and AI assistants can read the loop without talking to the hub.
        /// </summary>
        private void PublishFiles()
        {
            var now = DateTime.UtcNow;
            var snap = Latest;
            try
            {
                if (snap.Connected != _lastStatusConnected || (now - _lastStatusWrite).TotalMilliseconds >= StatusEveryMs)
                {
                    StatusFile.Write(Read(s => StatusFile.Build(snap, s, true, DateTimeOffset.Now)));
                    _lastStatusWrite = now;
                    _lastStatusConnected = snap.Connected;
                }
                if ((now - _lastHistoryWrite).TotalMilliseconds >= HistoryEveryMs)
                {
                    var row = StatusFile.HistoryRow(snap, DateTimeOffset.Now);
                    if (row != null)
                    {
                        StatusFile.AppendHistory(row);
                        _lastHistoryWrite = now;
                    }
                }
                _statusErrorLogged = false;
            }
            catch (Exception ex)
            {
                // Never let a locked or read-only file get in the way of fan control.
                if (!_statusErrorLogged)
                {
                    _statusErrorLogged = true;
                    Log.Write("Couldn't write the status files: " + ex.Message);
                }
            }
        }

        private void WaitOrStop(int ms)
        {
            WaitHandle.WaitAny(new WaitHandle[] { _stop, _wake }, ms);
        }

        private bool TryConnect()
        {
            if (_transportFactory != null)
            {
                return Attach(_transportFactory, "simulated hub");
            }

            var pids = new List<ushort> { LinkHub.SystemHubProductId };
            foreach (var extra in Read(s => s.ExtraHubProductIds.ToList()))
            {
                pids.Add((ushort)extra);
            }

            List<HidDeviceInfo> found;
            try
            {
                found = HidDevice.Enumerate(LinkHub.CorsairVendorId, pids);
            }
            catch (Exception ex)
            {
                Publish(false, "Couldn't scan USB devices: " + ex.Message);
                return false;
            }

            var candidate = found
                .Where(d => d.OutputReportLength > 0 && d.InputReportLength > 0)
                .OrderByDescending(d => d.OutputReportLength == 513)
                .FirstOrDefault();

            if (candidate == null)
            {
                if (Latest.Connected || Latest.Status.StartsWith("Starting"))
                {
                    LogCorsairInventory();
                }
                Publish(false, "No iCUE LINK System Hub found. Is it plugged into USB?");
                return false;
            }

            return Attach(() => HidDevice.Open(candidate), candidate.ToString());
        }

        private bool Attach(Func<IHidTransport> open, string description)
        {
            LinkHub? hub = null;
            try
            {
                var hid = open();
                bool debug = Read(s => s.DebugPacketLog);
                hub = new LinkHub(hid, _guard, debug ? (Action<string>)(m => Log.Write(m)) : null);
                hub.Initialize();
                _hub = hub;
                _connectedAt = DateTime.UtcNow;
                _lastWritten.Clear();
                _applied.Clear();
                _stallTicks.Clear();
                Log.Write($"Connected: {description} firmware {hub.Firmware}");
                RefreshDeviceList();
                return true;
            }
            catch (LinkBusyException)
            {
                SafeCall(() => hub?.Dispose());
                _hub = null;
                Publish(false, "Hub is busy (another app is holding it). Retrying…");
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("Connect failed: " + ex.Message);
                Publish(false, "Found the hub but couldn't talk to it. Close iCUE and try again. (" + ex.Message + ")");
                SafeCall(() => hub?.Dispose());
                _hub = null;
                return false;
            }
        }

        private static void LogCorsairInventory()
        {
            try
            {
                var all = HidDevice.Enumerate(LinkHub.CorsairVendorId);
                Log.Write($"Corsair HID interfaces present ({all.Count}):");
                foreach (var d in all) Log.Write("  " + d);
            }
            catch { }
        }

        private void RefreshDeviceList()
        {
            var subs = _hub!.GetSubDevices();
            var list = new List<ChannelState>();
            bool added = false;

            lock (_settingsLock)
            {
                foreach (var sd in subs)
                {
                    var model = LinkModels.Lookup(sd.Model);
                    if (!LinkModels.IsKnown(sd.Model))
                    {
                        Log.Write($"Unknown LINK device model=0x{sd.Model:x2} variant=0x{sd.Variant:x2} ch={sd.Channel} id={sd.Id} (treated as pump-safe)");
                    }
                    var cs = new ChannelState
                    {
                        Channel = sd.Channel,
                        DeviceId = string.IsNullOrEmpty(sd.Id) ? $"ch{sd.Channel}" : sd.Id,
                        Model = model,
                        Variant = sd.Variant,
                        Controllable = model.CanControlSpeed,
                    };

                    var st = _settings.Find(cs.DeviceId);
                    if (st == null && model.CanControlSpeed)
                    {
                        st = DefaultsFor(cs);
                        _settings.Channels.Add(st);
                        added = true;
                    }
                    cs.DisplayName = st != null && st.Nickname.Length > 0 ? st.Nickname : model.Name;
                    list.Add(cs);
                }
                if (added)
                {
                    _settings.Sanitize();
                    _settings.Save();
                }
            }

            _channels = list;
            _lastDeviceList = DateTime.UtcNow;
        }

        private static ChannelSettings DefaultsFor(ChannelState cs)
        {
            bool pump = cs.Model.Kind == LinkKind.Pump;
            return new ChannelSettings
            {
                DeviceId = cs.DeviceId,
                Mode = pump ? ControlMode.Fixed : ControlMode.Curve,
                FixedPercent = pump ? 70 : 50,
                Curve = pump ? Curves.BalancedPump() : Curves.BalancedFan(),
                SensorDeviceId = "",
            };
        }

        private void Tick()
        {
            var hub = _hub!;
            if ((DateTime.UtcNow - _lastDeviceList).TotalMilliseconds > RefreshDeviceListEveryMs)
            {
                RefreshDeviceList();
            }

            var speeds = hub.GetSpeeds().ToDictionary(r => r.Channel);
            var temps = hub.GetTemperatures().ToDictionary(r => r.Channel);

            foreach (var ch in _channels)
            {
                ch.Rpm = speeds.TryGetValue(ch.Channel, out var s) && s.Available ? (int?)(int)s.Value : null;
                ch.Temp = temps.TryGetValue(ch.Channel, out var t) && t.Available ? (double?)t.Value : null;
            }

            AppSettings cfg;
            lock (_settingsLock) cfg = Clone(_settings);

            foreach (var ch in _channels)
            {
                var nick = cfg.Find(ch.DeviceId)?.Nickname;
                ch.DisplayName = string.IsNullOrEmpty(nick) ? ch.Model.Name : nick!;
            }

            var (coolant, coolantSrc) = AutoCoolant();
            UpdateAlarms(cfg, coolant);

            bool force100 = _fullBlast || _tempAlarm || _stallAlarm;
            var targets = new Dictionary<int, byte>();

            foreach (var ch in _channels.Where(c => c.Controllable))
            {
                var st = cfg.Find(ch.DeviceId) ?? DefaultsFor(ch);
                bool pump = ch.Model.Kind == LinkKind.Pump;
                double target;
                string note;

                if (force100)
                {
                    target = 100;
                    note = _fullBlast ? "Full blast" : "Safety override";
                }
                else if (st.Mode == ControlMode.Fixed)
                {
                    target = st.FixedPercent;
                    note = "Fixed";
                }
                else
                {
                    var src = SensorFor(st, coolant);
                    if (src == null)
                    {
                        target = 100;
                        note = "Sensor missing → 100%";
                    }
                    else
                    {
                        target = Curves.Evaluate(st.Curve, src.Value);
                        note = $"Curve @ {src.Value:0.0}°C";
                    }
                }

                if (pump && target < cfg.PumpMinimumPercent)
                {
                    target = cfg.PumpMinimumPercent;
                }

                // Smooth changes so fans don't surge on every small blip; safety jumps straight up.
                double prev = _applied.TryGetValue(ch.Channel, out var p) ? p : target;
                double next = force100 ? target
                    : target > prev ? Math.Min(target, prev + RampUpPerTick)
                    : Math.Max(target, prev - RampDownPerTick);
                _applied[ch.Channel] = next;

                byte pct = (byte)Math.Round(Math.Max(0, Math.Min(100, next)));
                targets[ch.Channel] = pct;
                ch.AppliedPercent = pct;
                ch.DriverNote = note;
            }

            bool changed = targets.Count != _lastWritten.Count || targets.Any(kv => !_lastWritten.TryGetValue(kv.Key, out var old) || old != kv.Value);
            if (changed || (DateTime.UtcNow - _lastWrite).TotalMilliseconds > RewriteEveryMs)
            {
                hub.SetFixedPercent(targets);
                _lastWritten = targets;
                _lastWrite = DateTime.UtcNow;
            }

            CheckPumpStall(cfg);

            var pumpRpm = _channels.Where(c => c.Model.Kind == LinkKind.Pump && c.Rpm.HasValue).Select(c => c.Rpm).FirstOrDefault();
            Latest = new HubSnapshot
            {
                Connected = true,
                Status = $"Connected · {_channels.Count} LINK device{(_channels.Count == 1 ? "" : "s")}",
                Firmware = hub.Firmware,
                Channels = _channels.Select(CloneState).ToList(),
                CoolantTemp = coolant,
                CoolantSource = coolantSrc,
                PumpRpm = pumpRpm,
                FullBlast = _fullBlast,
                Alarm = _stallAlarm ? "Pump RPM too low!" : _tempAlarm ? $"Coolant ≥ {cfg.AlarmCoolantTemp:0}°C" : null,
                LockInfo = _guard.Description,
            };
        }

        /// <summary>Best guess at loop coolant temp: pump/res sensor, then a block sensor, then the hottest sensor.</summary>
        private (double?, string) AutoCoolant()
        {
            var pump = _channels.FirstOrDefault(c => c.Model.Kind == LinkKind.Pump && c.Temp.HasValue);
            if (pump != null) return (pump.Temp, pump.DisplayName);
            var block = _channels.FirstOrDefault(c => c.Model.Kind == LinkKind.Sensor && c.Temp.HasValue);
            if (block != null) return (block.Temp, block.DisplayName);
            var hottest = _channels.Where(c => c.Temp.HasValue).OrderByDescending(c => c.Temp).FirstOrDefault();
            if (hottest != null) return (hottest.Temp, hottest.DisplayName + " (hottest sensor)");
            return (null, "");
        }

        private double? SensorFor(ChannelSettings st, double? coolant)
        {
            if (string.IsNullOrEmpty(st.SensorDeviceId)) return coolant;
            var dev = _channels.FirstOrDefault(c => c.DeviceId == st.SensorDeviceId);
            return dev?.Temp;
        }

        private void UpdateAlarms(AppSettings cfg, double? coolant)
        {
            if (coolant.HasValue)
            {
                if (!_tempAlarm && coolant.Value >= cfg.AlarmCoolantTemp)
                {
                    _tempAlarm = true;
                    Notify("Coolant is hot!", $"{coolant.Value:0.0}°C — everything to 100% until it drops.", true);
                }
                else if (_tempAlarm && coolant.Value <= cfg.AlarmCoolantTemp - 2)
                {
                    _tempAlarm = false;
                    Notify("Coolant back to normal", $"{coolant.Value:0.0}°C — curves are back in charge.", false);
                }
            }
        }

        private void CheckPumpStall(AppSettings cfg)
        {
            if (cfg.PumpStallRpm <= 0 || (DateTime.UtcNow - _connectedAt).TotalMilliseconds < StallGraceMs)
            {
                return;
            }

            bool anyStalled = false;
            foreach (var ch in _channels.Where(c => c.Model.Kind == LinkKind.Pump && c.Controllable))
            {
                if (!ch.Rpm.HasValue) continue;
                int n = _stallTicks.TryGetValue(ch.Channel, out var v) ? v : 0;
                n = ch.Rpm.Value < cfg.PumpStallRpm ? n + 1 : 0;
                _stallTicks[ch.Channel] = n;
                if (n >= StallTicksToAlarm) anyStalled = true;
            }

            if (anyStalled && !_stallAlarm)
            {
                _stallAlarm = true;
                Notify("Pump looks stalled!", "Pump RPM is near zero. Fans + pump forced to 100%. Check the pump now!", true);
            }
            else if (!anyStalled && _stallAlarm && _stallTicks.Values.All(x => x == 0))
            {
                _stallAlarm = false;
                Notify("Pump is spinning again", "Normal control resumed.", false);
            }
        }

        private void Notify(string title, string text, bool warn)
        {
            Log.Write($"NOTICE: {title} {text}");
            Notifications.Enqueue(new Notification { Title = title, Text = text, IsWarning = warn });
        }

        private void Publish(bool connected, string status)
        {
            Latest = new HubSnapshot { Connected = connected, Status = status, FullBlast = _fullBlast, LockInfo = _guard.Description };
        }

        private void Disconnect(string status)
        {
            SafeCall(() => _hub?.Dispose());
            _hub = null;
            _channels = new List<ChannelState>();
            _consecutiveErrors = 0;
            Publish(false, status);
        }

        private static void SafeCall(Action a)
        {
            try { a(); } catch (Exception ex) { Log.Write("(ignored) " + ex.Message); }
        }

        private static ChannelState CloneState(ChannelState c) => new ChannelState
        {
            Channel = c.Channel, DeviceId = c.DeviceId, Model = c.Model, Variant = c.Variant, Rpm = c.Rpm, Temp = c.Temp,
            Controllable = c.Controllable, AppliedPercent = c.AppliedPercent, DriverNote = c.DriverNote, DisplayName = c.DisplayName,
        };

        private static AppSettings Clone(AppSettings s) => new AppSettings
        {
            PumpMinimumPercent = s.PumpMinimumPercent,
            AlarmCoolantTemp = s.AlarmCoolantTemp,
            PumpStallRpm = s.PumpStallRpm,
            Channels = s.Channels.Select(c => new ChannelSettings
            {
                DeviceId = c.DeviceId, Nickname = c.Nickname, Mode = c.Mode, FixedPercent = c.FixedPercent,
                Curve = Curves.Clone(c.Curve), SensorDeviceId = c.SensorDeviceId,
            }).ToList(),
        };

        // ------------------------------------------------------------------ shutdown

        public void Dispose()
        {
            _stop.Set();
            _thread?.Join(4000);
            SaveIfDirty();

            var hub = _hub;
            _hub = null;
            if (hub != null)
            {
                bool toHardware = Read(s => s.ReturnToHardwareModeOnExit);
                try
                {
                    if (toHardware)
                    {
                        hub.EnterHardwareMode();
                        Log.Write("Exit: handed the hub back to hardware mode.");
                    }
                    else
                    {
                        var safe = new Dictionary<int, byte>();
                        foreach (var ch in _channels.Where(c => c.Controllable))
                        {
                            safe[ch.Channel] = ch.Model.Kind == LinkKind.Pump ? (byte)100 : (byte)Math.Max(60, ch.AppliedPercent);
                        }
                        hub.SetFixedPercent(safe);
                        Log.Write("Exit: left safe fixed speeds (pump 100%, fans ≥ 60%).");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Exit handoff failed: " + ex.Message);
                }
                hub.Dispose();
            }

            try
            {
                var stopped = new HubSnapshot { Connected = false, Status = "LinkPilot is not running", LockInfo = _guard.Description };
                StatusFile.Write(Read(s => StatusFile.Build(stopped, s, false, DateTimeOffset.Now)));
            }
            catch { }

            _guard.Dispose();
        }
    }
}
