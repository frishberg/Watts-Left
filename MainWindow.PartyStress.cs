#if PARTY_STRESS
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace BatteryChecker;

/// <summary>
/// A stress tester for party mode, built only with -p:DefineConstants=PARTY_STRESS (never in a release). It does what an
/// impatient person would: flips party, graphs and stats at random moments (and on purpose while he's on a ladder or on
/// the ground), drops him all over the window, fakes plug changes, hides and shows the window. Meanwhile it watches him
/// every 50 ms and writes "STRESS FAIL" lines to party.log for anything wrong: stuck in mid-air, frozen, outside the
/// window, a ladder left standing, half-shrunk, folded legs, or the wrong end state once things go quiet.
/// </summary>
public sealed partial class MainWindow
{
    private int _stressFails, _stressChecks;
    private string _stressLast = "";

    private async Task PartyStressAsync()
    {
        if (Environment.GetEnvironmentVariable("WATTSLEFT_DEMO") is { } demo)
        {
            if (demo == "leaklab") { await LeakLabAsync(); Environment.Exit(0); }
            if (demo == "leaklab2") { await LeakLab2Async(); Environment.Exit(0); }
            if (demo == "leaklab3") { await LeakLab3Async(); Environment.Exit(0); }
            if (demo == "leaklab4") { await LeakLab4Async(); Environment.Exit(0); }
            // for filming: settings open, him out and dancing, then the dive home
            await Task.Delay(1500);
            OpenSettings();
            if (!Settings.PartyMode) SetParty(true);
            await Task.Delay(demo == "dance" ? 40000 : 5000);
            Log("DEMO dive");
            SetParty(false);
            await Task.Delay(7000);
            Environment.Exit(0);
        }
        var rng = new Random(Environment.GetEnvironmentVariable("WATTSLEFT_STRESS_SEED") is { } seed ? int.Parse(seed) : Environment.TickCount);
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("WATTSLEFT_STRESS_MINUTES"), out var m) ? m : 8;
        bool party0 = Settings.PartyMode, graphs0 = Settings.ShowGraphs, stats0 = Settings.ShowStats;
        _stressOffset = AppWindow.ClientSize.Height - _heightPx;
        Log($"STRESS start (client reads {_stressOffset} px over the asked-for height)");
        await Task.Delay(1500);
        OpenSettings();
        var watch = WatchAsync();
        var end = DateTime.UtcNow.AddMinutes(minutes);
        int round = 0;
        while (DateTime.UtcNow < end)
        {
            round++;
            // a burst of things, then quiet so we can check where he settles
            int burst = rng.Next(1, 7);
            for (int i = 0; i < burst; i++)
            {
                await Act(rng);
                await Task.Delay(rng.Next(10, 1400));
            }
            // aim at the reported bug: wait until he's on a ladder or on the ground, then move the floor
            if (rng.Next(3) == 0) await AimAtGround(rng);
            await Quiet(rng.Next(17000, 21000), round);
        }
        _stressStop = true;
        await watch;
        SetGraphs(graphs0, true); await Task.Delay(400);
        SetStats(stats0); await Task.Delay(400);
        if (Settings.PartyMode != party0) SetParty(party0);
        await Task.Delay(9000);
        Log($"STRESS done: {round} rounds, {_stressChecks} checks, {_stressFails} fails");
        Environment.Exit(_stressFails == 0 ? 0 : 1);
    }

    private bool _stressStop;
    private int _stressOffset;

    private async Task Act(Random rng)
    {
        if (!_settingsOpen) OpenSettings();
        switch (rng.Next(12))
        {
            case 0: case 1: case 2: _stressLast = "party"; SetParty(!Settings.PartyMode); break;
            case 3: case 4: _stressLast = "graphs"; SetGraphs(!Settings.ShowGraphs, true); break;
            case 5: _stressLast = "stats"; SetStats(!Settings.ShowStats); break;
            case 6: case 7: _stressLast = "drop"; if (Out) await FakeDrop(rng); break;
            case 8: _stressLast = "plug"; ReactToPlug(rng.Next(2) == 0); break;
            case 9: _stressLast = "hide/show"; HideFlyout(); await Task.Delay(rng.Next(50, 900)); ShowFlyout(); OpenSettings(); break;
            case 10: _stressLast = "close settings"; CloseSettings(); await Task.Delay(300); OpenSettings(); break;
            case 11: _stressLast = "graphs x2"; SetGraphs(!Settings.ShowGraphs, true); await Task.Delay(rng.Next(10, 120)); SetGraphs(!Settings.ShowGraphs, true); break;
        }
    }

    /// <summary>Pick him up, carry him somewhere for a moment, let go: the same steps the pointer handlers take.</summary>
    private async Task FakeDrop(Random rng)
    {
        if (_held) return;
        Begin();
        _held = true;
        PassthroughFor(RectOf(0, 0, Root.ActualWidth, Root.ActualHeight));
        Freeze();
        ResetFigure();
        HideLadder();
        if (!Settings.PartyMode) { Settings.PartyMode = true; SlideKnob(true, true); }
        _pendingParty = null;
        _ = Limbs(160, 150, -150, 8, -8);
        float x = (float)(rng.NextDouble() * (Root.ActualWidth + 60) - 30), y = (float)(rng.NextDouble() * (Root.ActualHeight + 60) - 30);
        Body.CenterPoint = new Vector3(13, 1.5f, 0);
        Body.Properties.InsertVector3("Translation", new Vector3(x, y, 0));
        Body.RotationAngleInDegrees = (float)(rng.NextDouble() * 60 - 30);
        await Task.Delay(rng.Next(50, 600));
        // sometimes the floor moves while he's in your hand
        if (rng.Next(4) == 0) { SetGraphs(!Settings.ShowGraphs, true); await Task.Delay(rng.Next(20, 300)); }
        _held = false;
        var ct = Begin();
        try { await DropAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Recover(ex); }
        finally { Done(ct); }
    }

    private async Task AimAtGround(Random rng)
    {
        if (!Out) { SetParty(true); await Task.Delay(400); }
        if (!Settings.PartyMode) { Settings.PartyMode = true; SlideKnob(true, true); }   // like a grab: picking him up mid-exit brings him back out
        _pendingParty = null;
        if (!Settings.ShowGraphs) { SetGraphs(true, true); await Task.Delay(400); }
        // drop him below the bar so he has to walk and climb
        _stressLast = "aim: drop low";
        Begin();
        Freeze(); ResetFigure(); HideLadder();
        float x = (float)(rng.NextDouble() * (Root.ActualWidth - 30));
        Body.CenterPoint = new Vector3(13, 28, 0);
        Body.Properties.InsertVector3("Translation", new Vector3(x, (float)Root.ActualHeight - 120, 0));
        var ct = Begin();
        _ = TumbleAsync(x, ct).ContinueWith(_ => { });
        bool onLadder = rng.Next(2) == 0;
        for (int i = 0; i < 200; i++)
        {
            await Task.Delay(30);
            if (onLadder ? _climbing && _onGround : _onGround && !_climbing && Math.Abs(Where().Y - GroundY()) < 2) break;
        }
        await Task.Delay(rng.Next(0, 500));
        _stressLast = onLadder ? "aim: graphs while climbing" : "aim: graphs while walking";
        Log("STRESS " + _stressLast);
        SetGraphs(false, true);
        if (rng.Next(3) == 0) { await Task.Delay(rng.Next(30, 600)); _stressLast += " + back on"; SetGraphs(true, true); }
        if (rng.Next(3) == 0) { await Task.Delay(rng.Next(30, 600)); _stressLast += " + stats"; SetStats(!Settings.ShowStats); }
    }

    private void Fail(string what)
    {
        _stressFails++;
        Log($"STRESS FAIL [{what}] after '{_stressLast}' at {Where()} floor={FloorY():0} ground={GroundY():0} party={Settings.PartyMode} out={Out} dancing={_dancing} onGround={_onGround} climbing={_climbing} ladder={Ladder.Visibility} scale={Body.Scale} opacity={Body.Opacity:0.00}");
    }

    private DateTime _stressToggledAt = DateTime.MinValue;   // any toggle, ours or a real click on the window

    private async Task Quiet(int ms, int round)
    {
        await Task.Delay(ms);
        // judge only once things have settled: not busy, and no toggle in the last 25 s (a full exit takes about 12 s)
        for (int i = 0; i < 200 && (Busy || _exiting || _edgeMoving || (DateTime.UtcNow - _stressToggledAt).TotalSeconds < 25); i++)
            await Task.Delay(250);
        _stressChecks++;
        // the window's height must match what the toggles say
        int want = Px(DesignHeight);
        if (Math.Abs(AppWindow.ClientSize.Height - _stressOffset - want) > 2) Fail($"window height {AppWindow.ClientSize.Height} - {_stressOffset} != {want}");
        if (Math.Abs(_heightPx - want) > 1) Fail($"asked-for height {_heightPx} != {want}");
        if ((Graphs.Visibility == Visibility.Visible) != Settings.ShowGraphs) Fail("graphs visibility doesn't match the toggle");
        if (_held) return;
        if (Settings.PartyMode)
        {
            if (!Out) { Fail("party on, but he's not out"); return; }
            if (!_dancing) { Fail("party on and quiet, but not dancing"); return; }
            var at = Where();
            if (at.Y < FloorY() - 26 || at.Y > FloorY() + 10) Fail("dancing off the bar (y)");
            if (at.X < FloorLeft() - 2 || at.X > FloorRight() + 2) Fail("dancing off the charged bar (x)");
            if (Ladder.Visibility == Visibility.Visible && LadderStanding()) Fail("ladder left standing");
            if (Body.Opacity < 0.99f || Math.Abs(Body.Scale.X - 1) > 0.01f || Math.Abs(Body.Scale.Y - 1) > 0.01f) Fail("shrunk or see-through");
            foreach (var leg in new[] { LimbVisuals()[2], LimbVisuals()[3] }) if (leg.Scale.Y < 0.99f) Fail("legs still folded");
        }
        else
        {
            if (Out) Fail("party off and quiet, but he's still out");
            if (Ladder.Visibility == Visibility.Visible && LadderStanding()) Fail("ladder left standing (party off)");
        }
        Log($"STRESS check {round}: ok so far ({_stressFails} fails)");
    }

    private bool LadderStanding() =>
        ElementCompositionPreview.GetElementVisual(Ladder).Properties.TryGetVector3("Translation", out var t) == Microsoft.UI.Composition.CompositionGetValueStatus.Succeeded && t.Y < 1;

    /// <summary>Every 50 ms: is he somewhere sane, and is he moving when he should be?</summary>
    private async Task WatchAsync()
    {
        Vector3 last = default;
        var still = DateTime.UtcNow;
        int frozenReported = 0;
        while (!_stressStop)
        {
            await Task.Delay(50);
            if (!Out || _held || !AppWindow.IsVisible) { still = DateTime.UtcNow; continue; }
            // read what's on screen: a running animation's stored value is its target, so sample through an expression-free path
            var at = Where();
            if (at.X < -40 || at.X > Root.ActualWidth + 10 || at.Y < -60 || at.Y > Root.ActualHeight + 10)
                if (_dancing) Fail("outside the window while dancing");
            // stuck: nothing is running for him, yet he's out and not dancing
            bool idle = _sequence is null || _sequence.IsCancellationRequested;
            if (idle && !_dancing && Settings.PartyMode)
            {
                if ((DateTime.UtcNow - still).TotalSeconds > 2.5 && frozenReported++ < 3) Fail("out, nothing running, not dancing (frozen)");
            }
            else { still = DateTime.UtcNow; frozenReported = 0; }
            last = at;
        }
    }

    // ------------------------------------------------------------ the leak lab
    // Runs each suspect operation thousands of times and reports what stays behind after full collections, per call.
    // A true native leak shows as a steady number of bytes per call; garbage that is only waiting for the collector
    // disappears after the forced collections. Results go to party.log as "LAB" lines.

    private static long Private() => System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;

    private static long SettleAndMeasure()
    {
        for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        GC.Collect();
        return Private();
    }

    private async Task Lab(string name, int n, Action op)
    {
        await Task.Delay(300);
        long before = SettleAndMeasure();
        long managedBefore = GC.GetTotalMemory(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < n; i++) op();
        long peak = Private();
        await Task.Delay(300);
        long after = SettleAndMeasure();
        // and once more after the UI thread has been idle, in case something is released on a later pass
        await Task.Delay(1500);
        long later = SettleAndMeasure();
        Log($"LAB {name,-34} n={n,6}  peak +{(peak - before) / 1024.0,8:0} KB  after GC +{(after - before) / 1024.0,8:0} KB  idle +{(later - before) / 1024.0,8:0} KB  => {(later - before) / (double)n,8:0.0} bytes/call  ({sw.ElapsedMilliseconds} ms)");
    }

    private async Task LeakLabAsync()
    {
        await Task.Delay(1500);
        Log("LAB start");
        var host = StoredText;
        int k = 0;

        await Lab("warm-up (ignore)", 2000, () => { _ = Brush("Ink"); });

        await Lab("TransformToVisual(Root).TransformPoint", 20000, () => { _ = InRoot(BarTrack); });
        await Lab("ActualOffset chain (alternative)", 20000, () => { _ = OffsetInRoot(BarTrack); });
        await Lab("Brush(key) theme lookup", 20000, () => { _ = Brush("Ink"); _ = Brush("InkFaint"); });
        await Lab("TextBlock.Text = new string", 20000, () => { host.Text = "41.3 Wh / 81.0 Wh " + (k++); });
        await Lab("StateText inlines rebuild", 3000, () =>
        {
            StateText.Inlines.Clear();
            StateText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Charging" + (k++) });
            StateText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " · full in 24m" });
        });
        await Lab("GetElementVisual(element)", 20000, () => { _ = ElementCompositionPreview.GetElementVisual(BarFill); });
        await Lab("Foreground = brush", 20000, () => { InText.Foreground = Brush(k++ % 2 == 0 ? "Ink" : "InkFaint"); });
        await Lab("DrawGraphs() (graphs redraw)", 400, () => { DrawGraphs(); });
        await Lab("UpdateRegions() (RectOf x4)", 400, () => { UpdateRegions(); });
        await Lab("tray.Set (new tip each call)", 400, () => { _tray.Set("76", "76% · Charging\nFull in 24m\nIn 38." + (k++ % 10) + " W · Out 9.8 W"); });
        await Lab("Limbs() (4 anims + batch)", 2000, () => { _ = Limbs(1, 40, -40, 18, -18); });
        await Lab("Compositor animation + Play()", 2000, () =>
        {
            var v = ElementCompositionPreview.GetElementVisual(BarFill);
            _ = Play(1, (v, "Opacity", Scalar(Enter, (1, 1f))));
        });
        await Lab("Storyboard (SlideKnob style)", 1000, () => { SlideKnob(PartyKnobSlide, PartyPill, k++ % 2 == 0, false); });
        await Lab("DispatcherQueue timer-less enqueue", 5000, () => { DispatcherQueue.TryEnqueue(() => { }); });
        Log("LAB done");
    }


    // ------------------------------------------------------------ the leak lab, round two: isolate and test fixes

    private static readonly Windows.Foundation.Point[] LabPoints = Enumerable.Range(0, 360).Select(i => new Windows.Foundation.Point(i * 0.8, 20 + 10 * Math.Sin(i / 9.0))).ToArray();

    private async Task LeakLab2Async()
    {
        await Task.Delay(1500);
        Log("LAB start (round two)");
        int k = 0;

        await Lab("control: empty loop", 20000, () => { });

        // ---- the graphs: which part leaks?
        await Lab("graphs A: build PointCollection only", 400, () => { var pc = new Microsoft.UI.Xaml.Media.PointCollection(); foreach (var pt in LabPoints) pc.Add(pt); });
        await Lab("graphs B: new collection assigned", 400, () => { var pc = new Microsoft.UI.Xaml.Media.PointCollection(); foreach (var pt in LabPoints) pc.Add(pt); ChargeLine.Points = pc; });
        await Lab("graphs C: same collection edited in place", 400, () =>
        {
            var pc = ChargeLine.Points ??= new Microsoft.UI.Xaml.Media.PointCollection();
            pc.Clear();
            foreach (var pt in LabPoints) pc.Add(pt);
        });
        await Lab("graphs D: Polygon new collection assigned", 400, () => { var pc = new Microsoft.UI.Xaml.Media.PointCollection(); foreach (var pt in LabPoints) pc.Add(pt); ChargeArea.Points = pc; });
        await Lab("graphs E: Polygon edited in place", 400, () =>
        {
            var pc = ChargeArea.Points ??= new Microsoft.UI.Xaml.Media.PointCollection();
            pc.Clear();
            foreach (var pt in LabPoints) pc.Add(pt);
        });
        await Lab("graphs F: DrawGraphs() as shipped", 400, () => { DrawGraphs(); });

        // ---- limb and body animations: which part leaks?
        var limbs = LimbVisuals();
        await Lab("anim A: create 4 animations only", 2000, () =>
        {
            for (int i = 0; i < 4; i++) { var an = Compositor.CreateScalarKeyFrameAnimation(); an.InsertKeyFrame(1f, 40f, Smooth); an.Duration = TimeSpan.FromMilliseconds(1); }
        });
        await Lab("anim B: create 4 + StartAnimation", 2000, () =>
        {
            for (int i = 0; i < 4; i++) { var an = Compositor.CreateScalarKeyFrameAnimation(); an.InsertKeyFrame(1f, 40f, Smooth); an.Duration = TimeSpan.FromMilliseconds(1); limbs[i].StartAnimation("RotationAngleInDegrees", an); }
        });
        var cached = new ScalarKeyFrameAnimation[4];
        for (int i = 0; i < 4; i++) cached[i] = Compositor.CreateScalarKeyFrameAnimation();
        await Lab("anim C: 4 cached animations restarted", 2000, () =>
        {
            for (int i = 0; i < 4; i++) { cached[i].InsertKeyFrame(1f, 30f + (k++ % 20), Smooth); cached[i].Duration = TimeSpan.FromMilliseconds(1); limbs[i].StartAnimation("RotationAngleInDegrees", cached[i]); }
        });
        await Lab("anim D: scoped batch only", 2000, () => { var b = Compositor.CreateScopedBatch(CompositionBatchTypes.Animation); b.End(); });
        await Lab("anim E: batch + completed handler", 2000, () => { var b = Compositor.CreateScopedBatch(CompositionBatchTypes.Animation); var t = new TaskCompletionSource(); b.Completed += (_, _) => t.TrySetResult(); b.End(); });
        await Lab("anim F: Limbs() as shipped", 2000, () => { _ = Limbs(1, 40, -40, 18, -18); });
        await Lab("anim G: Play() as shipped", 2000, () => { _ = Play(1, (limbs[0], "Opacity", Scalar(Enter, (1, 1f)))); });

        // ---- the small per-tick costs, and their fixes
        var inkBrush = Brush("Ink");
        string lastText = "";
        await Lab("tick A: Brush(key) lookup x2", 20000, () => { _ = Brush("Ink"); _ = Brush("InkFaint"); });
        await Lab("tick B: cached brush assigned", 20000, () => { InText.Foreground = inkBrush; });
        await Lab("tick C: Text set, same string", 20000, () => { StoredText.Text = "41.3 Wh / 81.0 Wh"; });
        await Lab("tick D: Text set, changing string", 20000, () => { StoredText.Text = "41.3 Wh / 81." + (k++ % 50); });
        await Lab("tick E: Text set only if changed", 20000, () => { var t = "41.3 Wh / 81." + ((k++ / 400) % 50); if (t != lastText) { lastText = t; StoredText.Text = t; } });
        await Lab("TransformToVisual x50000", 50000, () => { _ = InRoot(BarTrack); });
        await Lab("ActualOffset chain x50000", 50000, () => { _ = OffsetInRoot(BarTrack); });
        Log("LAB done (round two)");
    }


    // ------------------------------------------------------------ the leak lab, round three: slopes, not single readings

    /// <summary>
    /// Runs the operation in six rounds, settling (full collections plus idle time) after each, and reports the private bytes
    /// after every round. A real leak climbs by about the same amount every round; warm-up and delayed release flatten out.
    /// The number to read is the per-call figure from the last four rounds' average climb.
    /// </summary>
    private async Task Slope(string name, int n, Action op)
    {
        const int rounds = 6;
        var vals = new double[rounds];
        for (int r = 0; r < rounds; r++)
        {
            for (int i = 0; i < n; i++) op();
            await Task.Delay(200);
            for (int g = 0; g < 3; g++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            await Task.Delay(1200);
            GC.Collect();
            vals[r] = Private() / 1048576.0;
        }
        double climb = (vals[5] - vals[1]) / 4;   // MB per round over the last four rounds
        double minStep = Enumerable.Range(2, 4).Min(i => vals[i] - vals[i - 1]);
        Log($"LAB {name,-40} n={n,6}/round  MB: {string.Join(" ", vals.Select(v => v.ToString("0.0")))}  | climb {climb,6:0.00} MB/round = {climb * 1048576 / n,8:0} bytes/call  (smallest step {minStep:0.00})");
    }

    private async Task LeakLab3Async()
    {
        await Task.Delay(1500);
        Log("LAB start (round three: slopes)");
        int k = 0;
        var limbs = LimbVisuals();
        var inkBrush = Brush("Ink");

        await Slope("control: empty", 20000, () => { });
        await Slope("DrawGraphs() as shipped", 150, () => { DrawGraphs(); });
        await Slope("graphs: new PointCollection, not assigned", 150, () => { var pc = new Microsoft.UI.Xaml.Media.PointCollection(); foreach (var pt in LabPoints) pc.Add(pt); });
        await Slope("graphs: new collection assigned (Polyline)", 150, () => { var pc = new Microsoft.UI.Xaml.Media.PointCollection(); foreach (var pt in LabPoints) pc.Add(pt); ChargeLine.Points = pc; });
        await Slope("graphs: edited in place (Polyline)", 150, () => { var pc = ChargeLine.Points ??= new Microsoft.UI.Xaml.Media.PointCollection(); pc.Clear(); foreach (var pt in LabPoints) pc.Add(pt); });
        await Slope("TransformToVisual", 10000, () => { _ = InRoot(BarTrack); });
        await Slope("ActualOffset chain (alternative)", 10000, () => { _ = OffsetInRoot(BarTrack); });
        await Slope("Limbs() as shipped", 1000, () => { _ = Limbs(1, 40, -40, 18, -18); });
        await Slope("4 cached animations restarted, no batch", 1000, () =>
        {
            for (int i = 0; i < 4; i++) { cachedA[i] ??= Compositor.CreateScalarKeyFrameAnimation(); cachedA[i]!.InsertKeyFrame(1f, 30f + (k++ % 20), Smooth); cachedA[i]!.Duration = TimeSpan.FromMilliseconds(1); limbs[i].StartAnimation("RotationAngleInDegrees", cachedA[i]); }
        });
        await Slope("Play() as shipped", 1000, () => { _ = Play(1, (limbs[0], "Opacity", Scalar(Enter, (1, 1f)))); });
        await Slope("Brush(key) lookup x2", 20000, () => { _ = Brush("Ink"); _ = Brush("InkFaint"); });
        await Slope("Text set, changing string", 20000, () => { StoredText.Text = "41.3 Wh / 81." + (k++ % 50); });
        Log("LAB done (round three)");
    }

    private readonly ScalarKeyFrameAnimation?[] cachedA = new ScalarKeyFrameAnimation?[4];


    // ------------------------------------------------------------ the leak lab, round four: what a hidden tick does

    private async Task LeakLab4Async()
    {
        await Task.Delay(1500);
        Log("LAB start (round four: the per-tick work)");
        int k = 0;
        var reading = BatteryChecker.Battery.BatteryReader.Read();
        string hist = Path.Combine(Path.GetTempPath(), "lab-history.csv");

        await Slope("control: empty", 20000, () => { });
        await Slope("SystemPower.Watts() (PDH)", 2000, () => { _ = BatteryChecker.Battery.SystemPower.Watts(); });
        await Slope("BatteryDriver.Read() (IOCTLs)", 2000, () => { _ = BatteryChecker.Battery.BatteryDriver.Read(); });
        var opened = typeof(BatteryChecker.Battery.BatteryDriver).GetField("_opened", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        await Slope("BatteryDriver forced reopen (SetupDi)", 300, () => { opened.SetValue(null, DateTime.MinValue); _ = BatteryChecker.Battery.BatteryDriver.Read(); });
        await Slope("BatteryReader.Read() (whole read)", 1000, () => { _ = BatteryChecker.Battery.BatteryReader.Read(); });
        await Slope("Show(reading) (the whole UI update)", 1000, () => { Show(reading); });
        await Slope("tray.Set, new tip each call", 100, () => { _tray.Set("76", "76% · Charging\nFull in 24m\nIn 38." + (k++ % 10) + " W · Out 9.8 W"); });
        await Slope("TaskbarProgress.Show, changing percent", 300, () => { TaskbarProgress.Show(Hwnd, 40 + (k++ % 50)); });
        await Slope("File.AppendAllText (history line)", 500, () => { File.AppendAllText(hist, "2026-11-22T21:00:00.0000000Z,76.0,12.34,12.345,9.80" + Environment.NewLine); });
        try { File.Delete(hist); } catch { }
        Log("LAB done (round four)");
    }

    /// <summary>The same answer as InRoot, from the elements' layout offsets, without TransformToVisual.</summary>
    private Windows.Foundation.Point OffsetInRoot(FrameworkElement e)
    {
        double x = 0, y = 0;
        DependencyObject? d = e;
        while (d is UIElement ui && !ReferenceEquals(d, Root))
        {
            var o = ui.ActualOffset;
            x += o.X; y += o.Y;
            d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
        }
        return new Windows.Foundation.Point(x, y);
    }
}
#endif
