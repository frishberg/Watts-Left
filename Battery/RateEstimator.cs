namespace BatteryChecker.Battery;

/// <summary>
/// Works out the rate from how the level moves over time, for laptops whose firmware
/// doesn't report one. Many HP consumer machines ship with rate reporting switched off
/// in the BIOS, so every tool, HWiNFO included, reads 0 mW from them. The level still
/// moves, though, and the slope of that is the rate: what powercfg's battery report and
/// BatteryBar both do. Fed one value per tick (mWh, or percent for batteries that only
/// report relative units); answers in units per hour once it has seen the level change
/// at least twice, since a single step tells you nothing about how long the step took.
/// </summary>
public sealed class RateEstimator
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(20);

    private readonly List<(DateTime At, double Level)> _edges = new();   // the moments the level changed, and to what
    private double? _last;

    /// <summary>Units per hour: negative going down, positive going up. Null until it has enough to say.</summary>
    public double? PerHour { get; private set; }

    /// <summary>True once a level has been seen: "measuring" rather than "nothing to measure".</summary>
    public bool Started => _last is not null;

    public void Feed(DateTime now, double level)
    {
        if (_last is null)
        {
            _last = level;   // the first look is not a change: we may have joined halfway through a step, and counting it would make the first slope wildly steep
            return;
        }
        if (level != _last.Value)
        {
            _edges.Add((now, level));
            _last = level;
        }
        _edges.RemoveAll(e => now - e.At > Window);
        if (_edges.Count < 2)
        {
            PerHour = null;   // a level that hasn't moved twice yet says nothing about speed; a light load can sit on one percent for ten minutes
            return;
        }
        var first = _edges[0];
        var last = _edges[^1];
        var span = last.At - first.At;
        PerHour = span < MinSpan ? null : (last.Level - first.Level) / span.TotalHours;
        // The level hasn't moved since the last step, so it can't be moving faster than one step over that time: after
        // a heavy load stops, the old slope would otherwise hang on for up to fifteen minutes.
        double step = Math.Abs(last.Level - _edges[^2].Level);
        double sinceLast = (now - last.At).TotalHours;
        if (PerHour is { } p && sinceLast > 0 && step / sinceLast < Math.Abs(p))
            PerHour = Math.Sign(p) * step / sinceLast;
    }

    /// <summary>Plugged in or unplugged: the old slope belongs to the old state.</summary>
    public void Reset()
    {
        _edges.Clear();
        _last = null;
        PerHour = null;
    }
}
