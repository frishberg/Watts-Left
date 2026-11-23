namespace BatteryChecker.Battery;

/// <summary>
/// Decides, from the battery's own stored energy, whether it is really gaining or losing charge while plugged in.
///
/// Windows hands apps a signed rate, but on ACPI laptops the sign is not measured: the firmware reports a plain
/// magnitude and Windows adds the minus only when the firmware's "discharging" bit is set. Some firmware (Samsung's
/// among it) keeps the "charging" bit on whenever a charger is connected, so a laptop drawing more than a weak
/// charger gives reads as charging. Other platforms (Snapdragon) do measure the sign. The one thing every battery
/// reports truthfully is its stored energy, so that is the referee.
///
/// The test: over a recent window, the reported rate's size integrates to some energy I. If the battery really is
/// charging, the stored energy should have gone up by about I; if it is draining, down by about I. The two
/// predictions are 2·I apart, which separates them clearly even on batteries that report energy in coarse steps.
/// The judge never guesses: until the window holds enough evidence it says nothing.
/// </summary>
public sealed class DirectionJudge
{
    private const double SkipAfterChangeSeconds = 10;   // after a plug change, resume or battery swap, the first readings are stale
    private const double MaxWindowSeconds = 600;        // the furthest back the evidence reaches
    private const double MinWindowSeconds = 10;
    private const double MatchTolerance = 0.4;          // the energy may differ from the prediction by 40 % (mA firmware, gauge lag)
    private const double NearFullShare = 0.95;          // above this, a fall must be at least 1 % of the battery to count

    private readonly List<(double T, int Mwh, double Watts)> _samples = new();   // T in seconds since the window opened
    private double _clock;                // monotonic seconds, built from tick gaps so a clock change can't move it
    private DateTime _lastNow = DateTime.MinValue;
    private double _ignoreUntil;
    private int? _lastFull;
    private int? _lastMwh;
    private double _lastChangeAt;         // monotonic seconds of the last change in stored energy
    private int _step;                    // the smallest change in stored energy this battery has reported, in mWh

    /// <summary>+1 charging, -1 draining, 0 no evidence yet.</summary>
    public int Verdict { get; private set; }
    /// <summary>True when the verdict came from the stored energy matching the reported rate (so the rate's size is right).</summary>
    public bool RateMatched { get; private set; }
    /// <summary>The stored energy's own rate over the deciding window, W (signed); used when the reported size doesn't match.</summary>
    public double SlopeWatts { get; private set; }
    public int Step => _step;

    /// <summary>Start afresh: a plug change, sleep, a different battery, or a new full-charge figure.</summary>
    public void Restart()
    {
        _samples.Clear();
        _ignoreUntil = _clock + SkipAfterChangeSeconds;
        Verdict = 0;
        RateMatched = false;
    }

    /// <param name="now">Wall-clock time of this reading (only gaps between readings are used).</param>
    /// <param name="mwh">Stored energy, or null when the battery doesn't report it in mWh.</param>
    /// <param name="fullMwh">The full-charge figure; a change means the gauge recalibrated.</param>
    /// <param name="rateWatts">The size of the reported rate (sign ignored), or null when there is none.</param>
    public void Feed(DateTime now, int? mwh, int? fullMwh, double? rateWatts)
    {
        double gap = _lastNow == DateTime.MinValue ? 0 : (now - _lastNow).TotalSeconds;
        _lastNow = now;
        if (gap < 0 || gap > 5) Restart();              // a clock change, sleep or a stalled read: the window can't span it
        _clock += Math.Clamp(gap, 0, 5);

        if (mwh is not { } e || fullMwh is not { } full || full <= 0)
        {
            Verdict = 0;
            return;
        }
        if (_lastFull != full) { _lastFull = full; Restart(); }

        if (_lastMwh is { } prev && prev != e)
        {
            int delta = Math.Abs(e - prev);
            if (_step == 0 || delta < _step) _step = delta;
            // A change far beyond what the reported power could have done since the last change is the gauge correcting
            // itself (Dell Latitudes have been seen to drop from 50 % to 5 %), not energy flowing: start the window again,
            // so a correction downward while charging can never read as a drain. Measured from the last change, not the
            // last read, so a gauge that refreshes only once a minute isn't mistaken for one that jumps.
            double since = Math.Max(_clock - _lastChangeAt, 1);
            double plausible = 3 * Math.Max(rateWatts ?? 0, 5) * since / 3.6 + 2 * _step;
            if (delta > plausible) Restart();
            _lastChangeAt = _clock;
        }
        _lastMwh = e;
        if (_clock < _ignoreUntil) return;

        _samples.Add((_clock, e, rateWatts ?? 0));
        while (_samples.Count > 0 && _clock - _samples[0].T > MaxWindowSeconds) _samples.RemoveAt(0);
        Decide(full);
    }

    /// <summary>The freshest window, counted back from now, that holds enough evidence to decide.</summary>
    private void Decide(int full)
    {
        Verdict = 0;
        RateMatched = false;
        if (_samples.Count < 2 || _step == 0) return;
        var end = _samples[^1];
        double integral = 0;   // mWh the reported rate's size accounts for, from the candidate start to now
        int updates = 0;
        for (int i = _samples.Count - 2; i >= 0; i--)
        {
            var a = _samples[i];
            var b = _samples[i + 1];
            integral += a.Watts * (b.T - a.T) / 3.6;
            if (a.Mwh != b.Mwh) updates++;
            double span = end.T - a.T;
            if (span < MinWindowSeconds || updates < 2) continue;

            double observed = end.Mwh - a.Mwh;
            double minFall = end.Mwh >= NearFullShare * full ? Math.Max(full / 100.0, 3 * _step) : 3 * _step;
            SlopeWatts = observed / span * 3.6;

            if (2 * integral >= 3 * _step)
            {
                double tolerance = MatchTolerance * integral + _step;
                if (Math.Abs(observed - integral) <= tolerance) { Verdict = +1; RateMatched = true; return; }
                if (Math.Abs(observed + integral) <= tolerance && -observed >= minFall) { Verdict = -1; RateMatched = true; return; }
            }
            // the reported size doesn't fit either way (or there's barely any rate): the energy's own direction still counts
            if (observed >= 3 * _step && span >= 60) { Verdict = +1; return; }
            if (-observed >= minFall && span >= 60) { Verdict = -1; return; }
        }
    }

    public string Describe() =>
        $"step {(_step == 0 ? "?" : _step + " mWh")} · {_samples.Count} samples over {(_samples.Count > 1 ? _samples[^1].T - _samples[0].T : 0):0} s · " +
        $"verdict {(Verdict > 0 ? "charging" : Verdict < 0 ? "draining" : "none")}{(Verdict != 0 ? (RateMatched ? " (matches rate)" : $" (energy {SlopeWatts:+0.0;-0.0} W)") : "")}";
}
