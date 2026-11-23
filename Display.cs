using BatteryChecker.Battery;

namespace BatteryChecker;

/// <summary>
/// Every word and number the top of the window and the tray show, decided from one Reading. No UI in here, so
/// each state (on battery, charging, full, holding at a limit, draining, just plugged in, no rate, no meter) can be
/// checked in a test.
/// </summary>
public static class Display
{
    public sealed record Top(string In, bool InLit, string InSub, string Out, bool OutLit, string OutSub, string Net, bool NetLit, string NetSub);

    /// <summary>
    /// IN is what the charger puts out, OUT is what the laptop uses, NET beneath them is what the battery sees: IN minus
    /// OUT, positive filling, negative emptying. NET is the one number the battery reports directly; on the charger OUT
    /// is the processor meter plus the learned overhead, and IN is OUT plus NET. A number nobody can know shows a dash,
    /// never a made-up zero, and the caption says why.
    /// </summary>
    public static Top TopRow(Reading r)
    {
        string? noRate = r.Watts is not null ? null : r.Source switch { WattsSource.Measuring => "measuring…", WattsSource.Confirming => "confirming…", _ => "not reported" };
        string? noMeter = !r.OnAc || r.LaptopWatts is not null ? null : r.Draw == DrawState.Unavailable ? "not available" : "estimating…";
        string estTag = r.Source == WattsSource.Estimated ? "est. " : "";
        // Just plugged in or unplugged, the battery can still report the old direction for a moment; the numbers
        // built on it wait for it to turn instead of showing "on battery" with watts going in, or the reverse.
        bool waiting = r.Settling;

        double? charger = !r.OnAc ? 0 : waiting ? null : r.ChargerWatts;
        string inSub = !r.OnAc ? "unplugged" : noMeter ?? (charger is null && !waiting ? noRate : null) ?? "charger";

        double? laptop = waiting && !r.OnAc ? null : r.LaptopWatts;
        string outSub = !r.OnAc ? (waiting ? "laptop" : noRate ?? $"{estTag}laptop") : noMeter ?? "est. laptop";

        double? net = waiting ? null : r.Watts;
        string netSub = waiting ? "" : noRate ?? (estTag.Length > 0 ? "est." : "");

        return new Top(
            W(charger), charger > 0, inSub,
            W(laptop), laptop > 0, outSub,
            net is { } n ? $"{n:0.0;-0.0;0.0} W" : "—", net is not (null or 0), netSub);
    }

    /// <summary>The line under the percent. <paramref name="hours"/> is the time to full or to empty, already formatted.</summary>
    public static string StateLine(Reading r, string hours) =>
        r.Charging ? $"Charging · full in {hours}"
        : r.Draining ? $"Plugged in · draining · {hours} left"
        : r.JustPlugged || r is { OnAc: true, Source: WattsSource.Confirming } ? "Plugged in"
        : r.OnAc ? (r.Percent >= 99 ? "Plugged in · full" : r.ChargerTooWeak ? "Plugged in · weak charger" : "Plugged in · holding")
        : $"On battery · {hours} left";

    /// <summary>
    /// The tray icon's tooltip, in short lines you can take in at a glance:
    ///   76% · Charging
    ///   Full in 24m
    ///   In 38.5 W · Out 9.8 W · Net +28.7 W
    /// The watts line only has what is known (on battery just Out, since Net would only repeat it).
    /// </summary>
    public static string Tip(Reading r, string state)
    {
        // the state line is "Charging · full in 24m": the status first, the time (if any) as its own line
        string status = state, time = "";
        int cut = state.LastIndexOf(" · ", StringComparison.Ordinal);
        if (cut > 0 && (state.EndsWith(" left", StringComparison.Ordinal) || state.Contains("full in", StringComparison.Ordinal)))
        {
            status = state[..cut];
            time = state[(cut + 3)..];
            time = char.ToUpperInvariant(time[0]) + time[1..];
        }
        var lines = new List<string> { $"{r.Percent:0}% · {status}" };
        if (time.Length > 0) lines.Add(time);

        var top = TopRow(r);
        var watts = new List<string>();
        if (r.OnAc && top.InLit) watts.Add($"In {top.In}");
        if (top.OutLit) watts.Add($"Out {top.Out}");
        if (r.OnAc && top.NetLit) watts.Add($"Net {(top.Net.StartsWith('-') ? top.Net : "+" + top.Net)}");
        if (watts.Count > 0) lines.Add(string.Join(" · ", watts));

        string tip = string.Join("\n", lines);
        return tip.Length <= 127 ? tip : tip[..127];   // the tray truncates at 128 characters anyway; never hand it more
    }

    private static string W(double? w) => w is { } v ? $"{v:0.0} W" : "—";
}
