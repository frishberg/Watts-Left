// Watt's Left battery stress test. Replays simulated laptops through the app's real interpreter, direction judge,
// draw model and display code, four reads a second like the app, and checks what the window would show at every
// tick. Each simulated firmware behaviour comes from real laptops (see the comments by each scenario).
//   dotnet run -c Release          (from this folder)       add -v for timelines
using System.Globalization;
using BatteryChecker;
using BatteryChecker.Battery;

namespace BatteryChecker
{
    /// <summary>Stands in for the app's settings: kept in memory, cleared per scenario.</summary>
    public static class Settings
    {
        public static bool IsPackaged => false;
        public static int? LearnedOverheadMilliwatts { get; set; }
        public static int? LearnedOverheadSeconds { get; set; }
        public static int? FlagWrongCount { get; set; }
        public static bool FlagUnreliable { get; set; }
        public static void Clear() { LearnedOverheadMilliwatts = null; LearnedOverheadSeconds = null; FlagWrongCount = null; FlagUnreliable = false; }
    }
}

namespace BatteryChecker.Battery
{
    public static class SystemPower
    {
        public static bool Available { get; set; } = true;
        public static bool WholePlatform { get; set; }
        public static string Description => "stub";
        public static double? Watts() => null;
    }
}

/// <summary>How the firmware fills in the charging/discharging flags.</summary>
enum Flags
{
    Honest,              // what Microsoft requires: charging only while charging, discharging only while discharging
    AcMeansCharging,     // Samsung (and Apple's EC): plugged in and not idle means "charging", whichever way current flows
    ChargingAtZero,      // Huawei: "charging" stays set at 0 W while holding at a charge limit
}

/// <summary>Where the rate's sign comes from.</summary>
enum Sign
{
    FromFlags,           // ACPI + Windows' standard battery driver: a magnitude, negative only if the discharging flag is set
    Measured,            // Snapdragon: a genuinely measured signed rate
    PositiveAlways,      // old firmware that reports discharge as a positive number with the discharging flag set
}

enum Rate { Normal, Zero, Unknown }

/// <summary>A pretend laptop. Everything the battery driver would report comes from here.</summary>
sealed class Laptop
{
    public bool OnAc;
    public double Draw = 20;               // what the whole laptop really uses, W
    public double Overhead = 9;            // the part the processor meter doesn't see, W
    public double ChargerMax = 65;         // what the charger can give, W
    public double MaxCharge = 60;          // what the battery accepts, W
    public double Mwh = 50000, Full = 80000, Design = 80000;
    public double LimitPercent = 100;      // a charge limit (Samsung 85, Lenovo 60)
    public double LimitSagW;               // above the limit on AC, drain at this rate (HP Maximize, Surface Smart Charging)
    public Flags FlagMode = Flags.Honest;
    public Sign SignMode = Sign.FromFlags;
    public Rate RateMode = Rate.Normal;
    public bool Relative;                  // capacities in relative units, not mWh
    public int StepMwh = 1;                // the gauge's capacity resolution
    public double UpdateSeconds = 0;       // how often the reported capacity refreshes (0: every read)
    public double LagSeconds;              // after a plug change, the rate keeps the old direction this long
    public double ZeroAfterPlugSeconds;    // after a plug change, the rate reads 0 this long
    public bool NoMeter, Psys;
    public double? GarbageRate;            // report this rate (mW) once
    public double? JumpMwh;                // the gauge corrects itself by this much, once
    public double? FreezeCapacityAbove;    // the gauge holds capacity here (RSOCL at 99 %) while current still flows
    public int Cycles = 53;
    double _lastNet, _lagUntil, _zeroUntil, _t, _nextUpdate = double.MinValue;
    uint _shownCapacity;

    public double Percent => 100 * Mwh / Full;

    public double TrueNet
    {
        get
        {
            if (!OnAc) return -Draw;
            double spare = ChargerMax - Draw;
            if (spare < 0) return spare;                                              // can't keep up: the battery supplies the rest
            if (LimitSagW > 0 && Percent > LimitPercent) return -LimitSagW;           // a battery-care setting running it down
            if (Percent >= Math.Min(LimitPercent, 100)) return 0;                     // held at the limit, or full
            return Math.Min(spare, MaxCharge);
        }
    }

    public double? Cpu() => NoMeter ? null : Psys ? Draw - 0.5 : Math.Max(0.3, Draw - Overhead);

    public void Plug(bool on) { if (on == OnAc) return; _lagUntil = _t + LagSeconds; _zeroUntil = _t + ZeroAfterPlugSeconds; OnAc = on; }

    public ((BatteryDriver.Status, BatteryDriver.Info)?, double? percent) Step(double dt)
    {
        _t += dt;
        double net = TrueNet;
        Mwh = Math.Clamp(Mwh + net * 1000 * dt / 3600, 0, Full);
        if (JumpMwh is { } j) { Mwh = Math.Clamp(Mwh + j, 0, Full); JumpMwh = null; }
        double reported = _t < _lagUntil ? _lastNet : net;
        if (_t >= _lagUntil) _lastNet = net;
        if (_t < _zeroUntil) reported = 0;

        double pct = Percent;
        bool idle = Math.Abs(reported) < 0.05;
        bool charging, discharging;
        switch (FlagMode)
        {
            case Flags.AcMeansCharging: charging = OnAc ? !idle : false; discharging = !OnAc && !idle; break;
            case Flags.ChargingAtZero: charging = OnAc && (reported > 0.05 || (idle && pct < 99.3)); discharging = reported < -0.05 && !OnAc; break;
            default: charging = reported > 0.05; discharging = reported < -0.05; break;
        }
        uint state = (OnAc ? BatteryDriver.PowerOnLine : 0) | (charging ? BatteryDriver.Charging : 0) | (discharging ? BatteryDriver.Discharging : 0);

        double mag = Math.Abs(reported) * 1000;
        int rateMw = SignMode switch
        {
            Sign.Measured => (int)Math.Round(reported * 1000),
            Sign.PositiveAlways => (int)Math.Round(mag),
            _ => discharging ? -(int)Math.Round(mag) : charging ? (int)Math.Round(mag) : 0,
        };
        if (RateMode == Rate.Zero) rateMw = 0;
        if (RateMode == Rate.Unknown) rateMw = BatteryDriver.UnknownRate;
        if (GarbageRate is { } g) { rateMw = (int)g; GarbageRate = null; }

        // the gauge: whole steps, refreshed on its own schedule, sometimes held while current still flows
        if (_t >= _nextUpdate)
        {
            double shown = FreezeCapacityAbove is { } f && 100 * Mwh / Full >= f ? f / 100 * Full : Mwh;
            _shownCapacity = (uint)(Math.Floor(shown / StepMwh) * StepMwh);
            _nextUpdate = _t + UpdateSeconds;
        }
        uint caps = 0x80000000 | (Relative ? BatteryDriver.CapacityRelative : 0);
        var status = new BatteryDriver.Status(state, Relative ? (uint)Math.Round(pct) : _shownCapacity, 15500, rateMw);
        var info = new BatteryDriver.Info(caps, Relative ? 100 : (uint)Design, Relative ? 100 : (uint)Full, (uint)Cycles);
        return ((status, info), Math.Floor(pct));
    }
}

record Frame(double T, Reading? R, string State, Display.Top? Top);

static class Program
{
    static int _pass, _fail;
    static readonly List<string> Failures = new();
    static bool _verbose;

    static void Check(string scenario, bool ok, Func<string> what)
    {
        if (ok) { _pass++; return; }
        _fail++;
        string w; try { w = what(); } catch (Exception e) { w = "(" + e.Message + ")"; }
        Failures.Add($"{scenario}: {w}");
    }

    /// <summary>Run a laptop for some seconds, four ticks a second; optional events at given times.</summary>
    static List<Frame> Run(BatteryInterpreter interp, Laptop lap, double seconds, double start = 0, Dictionary<double, Action>? events = null,
        Func<double, bool>? driverFails = null, double tick = 0.25, Func<double, double>? clock = null)
    {
        var frames = new List<Frame>();
        var t0 = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        for (double t = start; t < start + seconds - 1e-9; t += tick)
        {
            if (events != null)
                foreach (var (at, act) in events)
                    if (at >= t && at < t + tick) act();
            var (raw, pct) = lap.Step(tick);
            if (driverFails?.Invoke(t) == true) raw = null;
            SystemPower.Available = !lap.NoMeter;
            SystemPower.WholePlatform = lap.Psys;
            var r = interp.Interpret(t0.AddSeconds(clock?.Invoke(t) ?? t), raw, pct, null, false, lap.Cpu());
            frames.Add(new Frame(t, r, r is null ? "No battery found" : Display.StateLine(r, "1h 00m"), r is null ? null : Display.TopRow(r)));
        }
        return frames;
    }

    static BatteryInterpreter Fresh() { Settings.Clear(); return new BatteryInterpreter(new DrawModel()); }
    static BatteryInterpreter Again() => new BatteryInterpreter(new DrawModel());   // a restart that keeps what was learned
    static double Num(string s) => s == "—" ? double.NaN : double.Parse(s.Replace(" W", ""), CultureInfo.CurrentCulture);
    static string Dump(Frame? f) => f is null ? "" : $"t={f.T,7:0.00} | {f.State,-34} | IN {f.Top?.In,-7} {f.Top?.InSub,-13} | OUT {f.Top?.Out,-7} {f.Top?.OutSub,-13} | NET {f.Top?.Net,-7} {f.Top?.NetSub}";
    static bool Neg(Frame f) => f.Top!.Net != "—" && Num(f.Top.Net) < 0;
    static bool Pos(Frame f) => f.Top!.Net != "—" && Num(f.Top.Net) > 0;

    /// <summary>States the line passed through, collapsing repeats, e.g. On battery → Plugged in → Charging.</summary>
    static List<string> Path(IEnumerable<Frame> frames)
    {
        var p = new List<string>();
        foreach (var f in frames)
        {
            string head = f.State.Split(" · ")[0] + (f.State.Contains("draining") ? " · draining" : f.State.Contains("holding") ? " · holding" : f.State.Contains("full") && !f.State.StartsWith("Charging") ? " · full" : "");
            if (p.Count == 0 || p[^1] != head) p.Add(head);
        }
        return p;
    }
    static string P(IEnumerable<Frame> f) => string.Join(" → ", Path(f));

    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _verbose = args.Contains("-v");
        Basics();
        Transitions();
        Firmware();
        WrongFlags();
        Limits();
        Robustness();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        foreach (var s in Failures) Console.WriteLine("FAIL " + s);
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ everyday behaviour

    static void Basics()
    {
        // Fresh install, plugged in and charging: numbers within a second, never "estimating…" after that.
        {
            var lap = new Laptop { OnAc = true, Draw = 25 };
            var f = Run(Fresh(), lap, 30);
            var late = f.Where(x => x.T >= 1).ToList();
            Check("fresh-plugged", late.All(x => x.Top!.In != "—" && x.Top.Out != "—" && x.Top.Net != "—"), () => "a dash after the first second: " + Dump(late.FirstOrDefault(x => x.Top!.In == "—" || x.Top.Out == "—" || x.Top.Net == "—")));
            Check("fresh-plugged", late.All(x => x.State.StartsWith("Charging")), () => "state: " + P(late));
            var last = f[^1].Top!;
            Check("fresh-plugged", Math.Abs(Num(last.Out) - 25) <= 4, () => $"OUT {last.Out} far from 25 W");
            Check("fresh-plugged", Math.Abs(Num(last.In) - Num(last.Out) - Num(last.Net)) <= 0.11, () => $"IN {last.In} ≠ OUT {last.Out} + NET {last.Net}");
            var tipLines = Display.Tip(f[^1].R!, "Charging · full in 1h 00m").Split((char)10);
            Check("tooltip-charging", tipLines.Length == 3 && tipLines[0].EndsWith("% · Charging") && tipLines[1] == "Full in 1h 00m"
                && tipLines[2].StartsWith("In ") && tipLines[2].Contains(" · Out ") && tipLines[2].Contains(" · Net +"), () => string.Join(" | ", tipLines));
        }
        // On battery: OUT is exactly the draw, IN is 0 unplugged, NET is minus the draw.
        {
            var lap = new Laptop { OnAc = false, Draw = 18.4 };
            var f = Run(Fresh(), lap, 10);
            var x = f[^1].Top!;
            Check("on-battery", x.Out == "18.4 W" && x.OutSub == "laptop" && x.In == "0.0 W" && x.InSub == "unplugged" && x.Net == "-18.4 W", () => Dump(f[^1]));
            Check("on-battery", f.All(y => y.State.StartsWith("On battery")), () => P(f));
            var batteryTip = Display.Tip(f[^1].R!, "On battery · 3h 10m left").Split((char)10);
            Check("tooltip-battery", batteryTip.Length == 3 && batteryTip[0].EndsWith("% · On battery") && batteryTip[1] == "3h 10m left" && batteryTip[2] == "Out 18.4 W", () => string.Join(" | ", batteryTip));
            var heldTip = Display.Tip(f[^1].R!, "Plugged in · full");
            Check("tooltip-no-time", heldTip.Split((char)10)[0].EndsWith("% · Plugged in · full"), () => heldTip);
        }
        // Honest chargers at several powers, with 10 mWh steps: never draining, never confirming, no lie counted.
        foreach (var (draw, charger) in new[] { (20.0, 22.5), (25.0, 45.0), (30.0, 100.0) })
        {
            var lap = new Laptop { OnAc = true, Draw = draw, ChargerMax = charger, StepMwh = 10 };
            var f = Run(Fresh(), lap, 300, tick: 0.5);
            var late = f.Where(y => y.T >= 15).ToList();
            Check($"honest-charge-{charger - draw}W", late.All(y => y.State.StartsWith("Charging") && Pos(y)), () => P(late) + " | " + Dump(late.FirstOrDefault(y => !Pos(y))));
            Check($"honest-charge-{charger - draw}W", (Settings.FlagWrongCount ?? 0) == 0, () => "counted a lie: " + Settings.FlagWrongCount);
        }
        // Full at 100 %, plugged in: "full", NET 0, IN is the laptop.
        {
            var lap = new Laptop { OnAc = true, Draw = 20, Mwh = 80000 };
            var f = Run(Fresh(), lap, 10);
            Check("full", f[^1].State == "Plugged in · full" && f[^1].Top!.Net == "0.0 W" && f[^1].Top.In == f[^1].Top.Out, () => Dump(f[^1]));
        }
        // Weak charger with honest flags: laptop 60 W on a 45 W charger. Draining within ~6 s, never "Charging".
        {
            var lap = new Laptop { OnAc = false, Draw = 60, ChargerMax = 45 };
            var f = Run(Fresh(), lap, 40, events: new() { [10] = () => lap.Plug(true) });
            var after = f.Where(y => y.T >= 10.25).ToList();
            Check("weak-honest", !Path(after).Contains("Charging"), () => P(after));
            Check("weak-honest", after.Where(y => y.T >= 17).All(y => y.State.StartsWith("Plugged in · draining") && y.Top!.Net == "-15.0 W"), () => P(after));
        }
        // Learning on battery, then plugged in: the estimate is close to the truth and survives a restart.
        {
            var lap = new Laptop { OnAc = false, Draw = 20, Overhead = 11 };
            var interp = Fresh();
            Run(interp, lap, 240);
            lap.Plug(true);
            var f = Run(interp, lap, 30, start: 240);
            Check("learned", Math.Abs(Num(f[^1].Top!.Out) - 20) <= 0.6, () => "OUT " + f[^1].Top!.Out + " vs 20");
            Check("learned-saved", Settings.LearnedOverheadMilliwatts is { } mw && Math.Abs(mw - 11000) < 600, () => "saved " + Settings.LearnedOverheadMilliwatts);
            var lap2 = new Laptop { OnAc = true, Draw = 30, Overhead = 11 };
            var g = Run(Again(), lap2, 5);
            Check("learned-restart", Math.Abs(Num(g[^1].Top!.Out) - 30) <= 0.8, () => "OUT after restart " + g[^1].Top!.Out);
        }
        // A whole-platform meter (Psys): the starting guess must not add 6 W on top.
        {
            var f = Run(Fresh(), new Laptop { OnAc = true, Draw = 25, Psys = true }, 10);
            Check("psys", Math.Abs(Num(f[^1].Top!.Out) - 25) <= 1.5, () => "OUT " + f[^1].Top!.Out);
        }
        // No processor meter: plugged in shows dashes with "not available", NET still works; on battery OUT is exact.
        {
            var f = Run(Fresh(), new Laptop { OnAc = true, Draw = 20, NoMeter = true }, 10);
            Check("no-meter", f[^1].Top!.InSub == "not available" && f[^1].Top.OutSub == "not available" && Pos(f[^1]), () => Dump(f[^1]));
            var g = Run(Fresh(), new Laptop { OnAc = false, Draw = 20, NoMeter = true }, 10);
            Check("no-meter-battery", g[^1].Top!.Out == "20.0 W", () => Dump(g[^1]));
        }
        // Other languages: a comma decimal separator, and nothing throws.
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var f = Run(Fresh(), new Laptop { OnAc = true, Draw = 20 }, 5);
            Check("de-DE", f[^1].Top!.Out.Contains(','), () => f[^1].Top!.Out);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        }
        // Long run: drained to near empty and charged to full, percent stays 0..100, ends full.
        {
            var lap = new Laptop { OnAc = false, Draw = 40, Mwh = 3000 };
            var interp = Fresh();
            var f = Run(interp, lap, 600, tick: 1);
            lap.Plug(true);
            f.AddRange(Run(interp, lap, 14000, start: 600, tick: 1));
            Check("long-run", f.All(y => y.R is not null && y.R.Percent is >= 0 and <= 100) && f[^1].State == "Plugged in · full", () => "end " + f[^1].State);
        }
    }

    // ------------------------------------------------------------ plugging and unplugging

    static void Transitions()
    {
        // Plug in with the rate lagging (still reporting the discharge): no draining or holding flash, no negative NET.
        foreach (double lag in new[] { 0.0, 1.0, 2.0, 4.5 })
        {
            var lap = new Laptop { OnAc = false, Draw = 22, LagSeconds = lag };
            var f = Run(Fresh(), lap, 40, events: new() { [10] = () => lap.Plug(true) });
            var path = Path(f);
            Check($"plug-in-lag{lag}", !path.Contains("Plugged in · draining") && !path.Contains("Plugged in · holding") && path[^1] == "Charging", () => P(f));
            var after = f.Where(y => y.T >= 10.25).ToList();
            Check($"plug-in-lag{lag}", after.All(y => !Neg(y)), () => Dump(after.FirstOrDefault(Neg)));
        }
        // Unplug with the rate lagging (still reporting the charge): "On battery" at once, no positive NET on battery.
        {
            var lap = new Laptop { OnAc = true, Draw = 22, LagSeconds = 2 };
            var f = Run(Fresh(), lap, 30, events: new() { [10] = () => lap.Plug(false) });
            var after = f.Where(y => y.T >= 10.25).ToList();
            Check("unplug-lag", after.All(y => y.State.StartsWith("On battery") && y.Top!.In == "0.0 W" && !Pos(y)), () => P(after));
            Check("unplug-lag", after.Where(y => y.T >= 13).All(y => y.Top!.Out == "22.0 W"), () => "OUT not settled");
        }
        // Aron's Samsung: started on the charger at 100 %, unplugged, 4.5 s of "discharging at 0 W" first.
        {
            var lap = new Laptop { OnAc = true, Draw = 20, Mwh = 80000, ZeroAfterPlugSeconds = 4.5 };
            var f = Run(Fresh(), lap, 30, events: new() { [10] = () => lap.Plug(false) });
            Check("unplug-zero-first", f.All(y => y.Top!.OutSub != "measuring…" && y.Top.NetSub != "measuring…"), () => Dump(f.FirstOrDefault(y => y.Top!.NetSub == "measuring…")));
            Check("unplug-zero-first", f.Where(y => y.T >= 15).All(y => y.Top!.Out == "20.0 W"), () => "OUT not real by 5 s");
        }
        // The second unplug of an evening: 3 s of 0 W on battery is never "NET 0.0 W".
        {
            var lap = new Laptop { OnAc = false, Draw = 20, Mwh = 70000, ZeroAfterPlugSeconds = 3 };
            var f = Run(Fresh(), lap, 60, events: new() { [20] = () => lap.Plug(true), [35] = () => lap.Plug(false) });
            Check("second-unplug-zero", f.Where(y => y.R is { OnAc: false }).All(y => y.Top!.Net != "0.0 W"), () => "NET 0.0 W on battery");
        }
        // Plugged in at 90 % with 5 s of 0 W first: dashes until the battery answers, then the numbers. No IN = OUT flicker.
        {
            var lap = new Laptop { OnAc = false, Draw = 30, Mwh = 72000, ZeroAfterPlugSeconds = 5 };
            var f = Run(Fresh(), lap, 40, events: new() { [10] = () => lap.Plug(true) });
            Check("plug-in-zero", f.Where(y => y.T >= 10.25 && y.T < 14.5).All(y => y.Top!.Net == "—" && y.Top.In == "—"), () => Dump(f.FirstOrDefault(y => y.T >= 10.25 && y.T < 14.5 && y.Top!.Net != "—")));
            Check("plug-in-zero", f.Where(y => y.T >= 17).All(y => y.State.StartsWith("Charging") && Pos(y)), () => P(f));
        }
        // A heavy spike mid-charge (2 s of drain, honest flags): no holding or draining flash.
        {
            var lap = new Laptop { OnAc = true, Draw = 20 };
            var f = Run(Fresh(), lap, 40, events: new() { [15] = () => lap.Draw = 80, [17] = () => lap.Draw = 20 });
            Check("spike", Path(f.Where(y => y.T >= 1)).SequenceEqual(new[] { "Charging" }), () => P(f));
        }
        // Rapid plug flapping (a loose cable): every state agrees with the plug, no negative IN.
        {
            var lap = new Laptop { OnAc = true, Draw = 25, LagSeconds = 1 };
            var ev = new Dictionary<double, Action>();
            for (int i = 0; i < 20; i++) { int k = i; ev[5 + k * 1.5] = () => lap.Plug(k % 2 == 1); }
            var f = Run(Fresh(), lap, 60, events: ev);
            Check("flapping", f.All(y => y.Top!.In == "—" || Num(y.Top.In) >= 0), () => "negative IN");
            Check("flapping", f.All(y => !(y.R!.OnAc == false && y.State.StartsWith("Plugged")) && !(y.R.OnAc && y.State.StartsWith("On battery"))), () => "state disagrees with the plug");
        }
    }

    // ------------------------------------------------------------ firmware that reports oddly (no lying flags)

    static void Firmware()
    {
        // HP: the rate is always 0. Plain dashes for 12 s, then "measuring", then a real figure from the slope; never a fake 0.
        {
            var f = Run(Fresh(), new Laptop { OnAc = false, Draw = 15, RateMode = Rate.Zero }, 900, tick: 1);
            Check("hp-zero-rate", f.All(y => y.Top!.Net != "0.0 W"), () => "fake 0.0 W");
            Check("hp-zero-rate", f[^1].R!.Source == WattsSource.Estimated && Math.Abs(Num(f[^1].Top!.Net) + 15) < 2, () => Dump(f[^1]));
            var g = Run(Fresh(), new Laptop { OnAc = true, Draw = 15, RateMode = Rate.Zero }, 15);
            Check("hp-zero-rate-ac", g.Where(y => y.T >= 12).All(y => y.Top!.InSub == "measuring…" && y.State.StartsWith("Charging")), () => Dump(g[^1]));
        }
        // Unknown rate (0x80000000) plugged in: measuring, never a number made up.
        {
            var f = Run(Fresh(), new Laptop { OnAc = true, Draw = 15, RateMode = Rate.Unknown }, 15);
            Check("unknown-rate", f.All(y => y.Top!.Net == "—" || y.R!.Source == WattsSource.Estimated), () => Dump(f[^1]));
        }
        // Garbage: one 900 W read is ignored.
        {
            var lap = new Laptop { OnAc = false, Draw = 20 };
            var interp = Fresh();
            Run(interp, lap, 5);
            lap.GarbageRate = 900_000;
            var f = Run(interp, lap, 5, start: 5);
            Check("garbage-rate", f.All(y => y.Top!.Net == "—" || Math.Abs(Num(y.Top.Net) + 20) < 0.2), () => Dump(f[0]));
        }
        // Old firmware: discharge reported as a positive number with the discharging flag set.
        {
            var f = Run(Fresh(), new Laptop { OnAc = false, Draw = 20, SignMode = Sign.PositiveAlways }, 5);
            Check("positive-discharge", f[^1].Top!.Net == "-20.0 W" && f[^1].Top!.Out == "20.0 W", () => Dump(f[^1]));
        }
        // Snapdragon: a measured sign. A weak charger reads negative straight away; nothing is ever counted as a lie.
        {
            var lap = new Laptop { OnAc = true, Draw = 40, ChargerMax = 30, SignMode = Sign.Measured };
            var f = Run(Fresh(), lap, 120);
            Check("snapdragon-weak", f.Where(y => y.T >= 7).All(y => y.State.StartsWith("Plugged in · draining")), () => P(f));
            Check("snapdragon-weak", (Settings.FlagWrongCount ?? 0) == 0 && !Settings.FlagUnreliable, () => "counted a lie on honest hardware");
        }
        // A battery in relative units: no Wh, no watts; honest dashes, no crash.
        {
            var f = Run(Fresh(), new Laptop { OnAc = false, Draw = 20, Relative = true }, 10);
            Check("relative", f[^1].Top!.Net == "—" && f[^1].Top.NetSub == "not reported" && f[^1].R!.RemainingMwh is null, () => Dump(f[^1]));
        }
        // VirtualBox-style coarse battery (500 mWh steps), honest flags, charging: no false verdict, no lie counted.
        {
            var f = Run(Fresh(), new Laptop { OnAc = true, Draw = 20, ChargerMax = 45, StepMwh = 500 }, 900, tick: 1);
            Check("coarse-500", f.Where(y => y.T >= 15).All(y => y.State.StartsWith("Charging")) && (Settings.FlagWrongCount ?? 0) == 0, () => P(f));
        }
    }

    // ------------------------------------------------------------ firmware that says "charging" while the battery drains

    static void WrongFlags()
    {
        // Aron's Samsung on 1 November: plugged into a charger that can't keep up. The firmware says "charging" and
        // Windows hands over a positive rate. The stored energy must overrule it within a minute.
        {
            var lap = new Laptop { OnAc = true, Draw = 24, ChargerMax = 20, Mwh = 8000, FlagMode = Flags.AcMeansCharging, StepMwh = 1 };
            var f = Run(Fresh(), lap, 300);
            var late = f.Where(y => y.T >= 60).ToList();
            Check("samsung-weak", late.All(y => y.State.StartsWith("Plugged in · draining") && Neg(y)), () => P(f) + " | " + Dump(late.FirstOrDefault(y => !Neg(y))));
            Check("samsung-weak", late.All(y => Math.Abs(Num(y.Top!.Net) + 4) < 0.6), () => "NET size: " + Dump(late[0]));
            Check("samsung-weak", late.All(y => Num(y.Top!.In) <= Num(y.Top.Out) + 0.11), () => "IN above OUT while draining: " + Dump(late[0]));
            Check("samsung-weak", Settings.FlagWrongCount == 1 && !Settings.FlagUnreliable, () => $"count {Settings.FlagWrongCount}, unreliable {Settings.FlagUnreliable}");
            // the second time plugged in, it's caught again: from now on this laptop's "charging" is never taken at its word
            var interp = Again();
            lap.Plug(false);
            Run(interp, lap, 30, start: 300);
            lap.Plug(true);
            Run(interp, lap, 120, start: 330);
            Check("samsung-second", Settings.FlagUnreliable, () => "not marked after two episodes: count " + Settings.FlagWrongCount);
            // the third time: no "Charging" at all, confirming until the energy decides, then draining
            var third = Again();
            lap.Plug(false);
            Run(third, lap, 20, start: 450);
            lap.Plug(true);
            var g = Run(third, lap, 120, start: 470);
            Check("samsung-third", g.All(y => !y.State.StartsWith("Charging") && !Pos(y)), () => P(g) + " | " + Dump(g.FirstOrDefault(Pos)));
            Check("samsung-third", g.Where(y => y.T >= 530).All(y => y.State.StartsWith("Plugged in · draining")), () => P(g));
            if (_verbose) foreach (var y in g.Where(y => y.T % 5 == 0)) Console.WriteLine(Dump(y));
        }
        // The same Samsung on a charger that does keep up: charging, confirmed, nothing counted.
        {
            var lap = new Laptop { OnAc = true, Draw = 24, ChargerMax = 60, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 300);
            Check("samsung-good", f.Where(y => y.T >= 2).All(y => y.State.StartsWith("Charging") && Pos(y)) && (Settings.FlagWrongCount ?? 0) == 0, () => P(f));
        }
        // A Samsung already marked unreliable, on a good charger: "confirming" briefly, then charging with numbers.
        {
            Settings.Clear(); Settings.FlagUnreliable = true; Settings.FlagWrongCount = 2;
            var lap = new Laptop { OnAc = false, Draw = 24, ChargerMax = 60, FlagMode = Flags.AcMeansCharging, StepMwh = 10 };
            var interp = Again();
            var f = Run(interp, lap, 120, events: new() { [10] = () => lap.Plug(true) });
            var after = f.Where(y => y.T >= 10.5).ToList();
            Check("unreliable-good", after.All(y => !Neg(y)), () => "showed draining on a good charger: " + Dump(after.FirstOrDefault(Neg)));
            Check("unreliable-good", after.Where(y => y.T >= 50).All(y => y.State.StartsWith("Charging") && Pos(y)), () => P(after));
        }
        // Samsung at 100 % on a 30 W charger while gaming: must still say draining, within a few minutes.
        {
            var lap = new Laptop { OnAc = true, Draw = 45, ChargerMax = 30, Mwh = 80000, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 400, tick: 0.5);
            Check("samsung-full-gaming", f.Where(y => y.T >= 240).All(y => y.State.StartsWith("Plugged in · draining")), () => P(f));
        }
        // Dell Hybrid Power: drain under load on AC. With an honest discharging flag it's immediate; with lying flags
        // the energy catches it. Either way it never shows charging once the drain has been running.
        foreach (var mode in new[] { Flags.Honest, Flags.AcMeansCharging })
        {
            var lap = new Laptop { OnAc = true, Draw = 150, ChargerMax = 130, Full = 90000, Mwh = 60000, FlagMode = mode };
            var f = Run(Fresh(), lap, 180);
            Check($"dell-hybrid-{mode}", f.Where(y => y.T >= 60).All(y => y.State.StartsWith("Plugged in · draining") && Neg(y)), () => P(f));
        }
        // A 1 %-step gauge (Compal) with lying flags and a weak charger: decides within 10 minutes and never flips back.
        {
            var lap = new Laptop { OnAc = true, Draw = 30, ChargerMax = 20, Full = 50000, Mwh = 30000, StepMwh = 500, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 900, tick: 1);
            int first = f.FindIndex(y => Neg(y));
            Check("coarse-weak", first >= 0 && f[first].T <= 600, () => "never decided: " + P(f));
            Check("coarse-weak", first >= 0 && f.Skip(first).All(y => !Pos(y)), () => "flipped back: " + P(f.Skip(Math.Max(first, 0))));
        }
        // Capacity refreshed only every 60 s (a slow EC) with lying flags: still caught, no flapping afterwards.
        {
            var lap = new Laptop { OnAc = true, Draw = 30, ChargerMax = 22, StepMwh = 10, UpdateSeconds = 60, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 900, tick: 1);
            int first = f.FindIndex(y => Neg(y));
            Check("slow-gauge", first >= 0 && f[first].T <= 300, () => "never decided: " + P(f));
            Check("slow-gauge", first >= 0 && f.Skip(first).All(y => !Pos(y)), () => "flipped back");
        }
        // The charger gets better mid-session (weak dock swapped for the real charger): charging returns within a minute.
        {
            var lap = new Laptop { OnAc = true, Draw = 30, ChargerMax = 20, Mwh = 40000, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 400, events: new() { [200] = () => lap.ChargerMax = 90 });
            Check("charger-swap", f.Where(y => y.T is >= 100 and < 200).All(Neg) && f.Where(y => y.T >= 270).All(y => y.State.StartsWith("Charging") && Pos(y)), () => P(f.Where(y => y.T >= 90)));
        }
    }

    // ------------------------------------------------------------ charge limits and holds (must never read as draining)

    static void Limits()
    {
        // ThinkPad holding at 80 % with neither flag (rate 0): holding.
        {
            var lap = new Laptop { OnAc = true, Draw = 20, Mwh = 64000, LimitPercent = 80 };
            var f = Run(Fresh(), lap, 300, tick: 0.5);
            Check("thinkpad-hold", f.Where(y => y.T >= 8).All(y => y.State == "Plugged in · holding"), () => P(f));
        }
        // Huawei: "charging" set at 0 W while holding at 80 %: holding, never draining, nothing counted.
        {
            var lap = new Laptop { OnAc = true, Draw = 20, Mwh = 64000, LimitPercent = 80, FlagMode = Flags.ChargingAtZero };
            var f = Run(Fresh(), lap, 300, tick: 0.5);
            Check("huawei-hold", f.All(y => !y.State.Contains("draining")) && (Settings.FlagWrongCount ?? 0) == 0, () => P(f));
        }
        // Samsung holding at its 85 % battery protection, lying flags: the energy is flat, so nothing is decided against it.
        {
            var lap = new Laptop { OnAc = true, Draw = 20, Mwh = 68000, LimitPercent = 85, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 300, tick: 0.5);
            Check("samsung-limit-hold", f.All(y => !y.State.Contains("draining")) && (Settings.FlagWrongCount ?? 0) == 0, () => P(f));
        }
        // HP Maximize / Surface Smart Charging: deliberately running down to the limit on AC, honest discharging flag.
        // Draining is the truth, so it shows; the wording (checked by hand) doesn't blame the charger.
        {
            var lap = new Laptop { OnAc = true, Draw = 20, Mwh = 72000, LimitPercent = 80, LimitSagW = 3 };
            var f = Run(Fresh(), lap, 120);
            Check("limit-sag-honest", f.Where(y => y.T >= 10).All(y => y.State.StartsWith("Plugged in · draining")), () => P(f));
        }
        // A gauge holding capacity at 99 % (RSOCL) while a 3 W trickle still flows: never "draining".
        {
            var lap = new Laptop { OnAc = true, Draw = 20, ChargerMax = 23, MaxCharge = 3, Mwh = 79000, FreezeCapacityAbove = 99, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 400, tick: 0.5);
            Check("rsocl-hold", f.All(y => !y.State.Contains("draining") && !Neg(y)), () => P(f));
        }
        // A tiny ±0.3 W wobble on a big pack: no verdict against the firmware, nothing counted.
        {
            var lap = new Laptop { OnAc = true, Draw = 20, ChargerMax = 20.3, Full = 99000, Mwh = 50000, StepMwh = 10, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 600, tick: 1);
            Check("tiny-net", f.All(y => !y.State.Contains("draining")) && (Settings.FlagWrongCount ?? 0) == 0, () => P(f));
        }
    }

    // ------------------------------------------------------------ the unexpected

    static void Robustness()
    {
        // A failed read or two (sleep, plug change) never flashes "No battery found"; a real absence does; a desktop has none.
        {
            var lap = new Laptop { OnAc = false, Draw = 20 };
            Check("driver-blip", Run(Fresh(), lap, 30, driverFails: t => t is >= 10 and < 11.5).All(y => y.R is not null), () => "flashed");
            Check("driver-gone", Run(Fresh(), lap, 30, driverFails: t => t >= 10).Where(y => y.T >= 16).All(y => y.R is null), () => "still a reading");
            Check("desktop", Run(Fresh(), lap, 5, driverFails: _ => true).All(y => y.R is null), () => "a desktop showed a reading");
            var interp = Fresh();
            Run(interp, lap, 20);
            Check("resume-blip", Run(interp, lap, 10, start: 7220, driverFails: t => t < 7221).All(y => y.R is not null), () => "flashed after resume");
        }
        // The gauge corrects itself by +2 % mid-drain on a weak charger with lying flags: no crash, no reversal to charging.
        {
            var lap = new Laptop { OnAc = true, Draw = 30, ChargerMax = 20, Mwh = 40000, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 300, events: new() { [150] = () => lap.JumpMwh = 1600 });
            Check("gauge-jump", f.Where(y => y.T >= 100).All(y => !Pos(y)), () => P(f.Where(y => y.T >= 100)));
        }
        // An honest laptop charging slowly when its gauge corrects itself down by 0.5 % (smaller than the old 1 % guard):
        // must not show draining, and must not count a lie. Then the same with a bigger 5 % drop (the Dell case).
        foreach (var (drop, label) in new[] { (-400.0, "0.5%"), (-4000.0, "5%") })
        {
            var lap = new Laptop { OnAc = true, Draw = 20, ChargerMax = 22, Mwh = 50000, StepMwh = 10, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 900, events: new() { [300] = () => lap.JumpMwh = drop }, tick: 0.5);
            Check($"gauge-drop-{label}", f.All(y => !y.State.Contains("draining") && !Neg(y)), () => P(f) + " | " + Dump(f.FirstOrDefault(Neg)));
            Check($"gauge-drop-{label}", (Settings.FlagWrongCount ?? 0) == 0, () => "counted a lie: " + Settings.FlagWrongCount);
        }
        // Sleep in the middle of a weak-charger drain (a two-hour gap): the window restarts, the verdict comes back.
        {
            var lap = new Laptop { OnAc = true, Draw = 30, ChargerMax = 20, Mwh = 60000, FlagMode = Flags.AcMeansCharging };
            var interp = Fresh();
            Run(interp, lap, 120);
            var f = Run(interp, lap, 120, start: 7320);
            Check("sleep-gap", f.Where(y => y.T >= 7380).All(Neg), () => P(f));
        }
        // The clock is moved back an hour mid-drain: nothing breaks, nothing flips to charging.
        {
            var lap = new Laptop { OnAc = true, Draw = 30, ChargerMax = 20, Mwh = 60000, FlagMode = Flags.AcMeansCharging };
            var f = Run(Fresh(), lap, 240, clock: t => t < 120 ? t : t - 3600);
            Check("clock-back", f.All(y => y.R is not null) && f.Where(y => y.T >= 200).All(Neg), () => P(f));
        }
        // Honest weak charger after the app has wrongly been marked unreliable: still correct (it waits, then drains).
        {
            Settings.Clear(); Settings.FlagUnreliable = true;
            var lap = new Laptop { OnAc = true, Draw = 40, ChargerMax = 30 };
            var f = Run(Again(), lap, 60);
            Check("unreliable-honest-weak", f.Where(y => y.T >= 7).All(y => y.State.StartsWith("Plugged in · draining")), () => P(f));
        }
    }
}
