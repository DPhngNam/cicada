namespace Cicada;

// 听哪里 (the Mac's RoomMic.swift and EchoMatch.swift): 电脑声音, 会议室 (a microphone hears the
// people at the table), or 电脑和会议室 (both, each with its own ListenEngine). The parts that don't
// touch a device live here so they test on the Mac.

/// The Mac's `listenSource` values.
public static class ListenSource
{
    public const string System = "system", Room = "room", Both = "both";
    public static string Parse(string? s) => s is Room or Both ? s : System;
}

/// Which microphone hears the room.
public static class RoomMicPlan
{
    public sealed record Input(string Id, bool Bluetooth = false, bool Virtual = false);

    /// The default input, unless it is the virtual cable (同声传译's) or a Bluetooth headset's (it
    /// hears the wearer, not the table, and drops to call quality while its mic is open); then the
    /// first other one, a headset only when nothing else is left.
    public static string? Pick(string? defaultId, IReadOnlyList<Input> inputs)
    {
        if (inputs.FirstOrDefault(i => i.Id == defaultId) is { Virtual: false, Bluetooth: false } d) return d.Id;
        return inputs.FirstOrDefault(i => !i.Virtual && !i.Bluetooth)?.Id ?? inputs.FirstOrDefault(i => !i.Virtual)?.Id;
    }
}

/// 会议室's noise suppression: the room mic opened in the communications category, so the driver's
/// noise suppression runs (Ming made up "So.", "Click." from key clicks on the plain stream, issue
/// #41), with its echo cancellation and gain control turned off. The Mac measured the system's
/// echo cancellation on a room mic losing most of the table's lines (RoomMic.swift › EchoGate), and
/// gain control lifts the room's noise with the voice. A stream that can't be had that way (no
/// noise suppression listed, or an echo canceller that stays on) is opened plain, as before.
public static class RoomDenoise
{
    public static readonly Guid Noise = new("6f64adbf-8211-11e2-8c70-2c27d7f001fa");
    public static readonly Guid Gain = new("6f64adc0-8211-11e2-8c70-2c27d7f001fa");
    /// `cicadactl roomdenoise off`, to compare the two on the same room.
    public static volatile bool Enabled = true;

    /// The states to ask for: noise suppression on, echo cancellation and gain control off, each
    /// only where the stream lists it in the other state and lets the app set it.
    public static List<(Guid Id, bool On)> Changes(IReadOnlyList<EchoCancel.AudioEffect> effects)
    {
        var want = new (Guid Id, bool On)[] { (Noise, true), (EchoCancel.Effect, false), (Gain, false) };
        return [.. want.Where(w => effects.Any(e => e.Id == w.Id && e.On != w.On && e.CanSetState))];
    }

    /// Worth keeping: noise suppression on and no echo cancellation. Gain control left on is
    /// tolerated (logged): it is the driver's, and noise suppression still runs before it. Intel
    /// Smart Sound lists NS and AEC both on and neither settable; kept anyway (2026-11-25, laptop
    /// mic, headphones, 10 lines read from 1 m after a minute of typing), it heard 0 of the 10 and
    /// still made up 2 lines from the typing, against 3 heard and 1 made up on the plain stream.
    public static bool Usable(IReadOnlyList<EchoCancel.AudioEffect>? effects) =>
        effects is not null && effects.Any(e => e.Id == Noise && e.On) && !effects.Any(e => e.Id == EchoCancel.Effect && e.On);
}

/// When a frame counts as speech. The computer's own audio is digital silence between words, so a
/// fixed -40 dBFS works there. A room mic always hears the room (a fan, the street), and a voice
/// across the table can be quieter than that: there the line sits at twice the room's own noise,
/// the quietest moment of each second taken low over the last half minute (VoiceGate in RoomMic.swift).
public sealed class VoiceGate
{
    public const double Fixed = 0.01, Lowest = 0.0015;
    readonly List<double> quietest = [];
    double second = double.PositiveInfinity, elapsed;

    public double Threshold
    {
        get
        {
            if (quietest.Count < 3) return 0.004;
            var floor = quietest.Order().ElementAt(quietest.Count / 5);
            return Math.Min(Fixed, Math.Max(Lowest, floor * 2));
        }
    }

    /// Takes one frame's level and length; true when it is over the line.
    public bool Voiced(double rms, double seconds)
    {
        var on = rms >= Threshold;
        second = Math.Min(second, rms);
        elapsed += seconds;
        if (elapsed >= 1 - 1e-9)
        {
            quietest.Add(second);
            if (quietest.Count > 30) quietest.RemoveAt(0);
            second = double.PositiveInfinity;
            elapsed = 0;
        }
        return on;
    }
}

/// The room mic as listening hears it: silence while `Muted` (you holding the talk key) or while
/// `Silenced` says so (电脑和会议室's echo gate). Silence rather than nothing keeps the segmenter's
/// clock in step.
public sealed class GatedSource : IAudioSource
{
    readonly IAudioSource inner;
    public Func<bool>? Silenced { get; set; }
    /// Each buffer's level (0–1) and length in seconds, before silencing.
    public Action<double, double>? Heard { get; set; }
    public volatile bool Muted;
    public event Action<short[]>? Samples;
    public event Action<string?>? Ended;
    /// The microphone's name, for the log and the panel.
    public string? Name { get; init; }

    public GatedSource(IAudioSource inner)
    {
        this.inner = inner;
        inner.Samples += buf =>
        {
            // What the mic really hears, for EchoGate's measurement, before any silencing.
            if (Heard is { } heard && buf.Length > 0)
            {
                double sum = 0;
                foreach (var v in buf) sum += (double)v * v;
                heard(Math.Sqrt(sum / buf.Length) / 32768, buf.Length / 16000.0);
            }
            Samples?.Invoke(Muted || Silenced?.Invoke() == true ? new short[buf.Length] : buf);
        };
        inner.Ended += why => Ended?.Invoke(why);
    }

    public void Start() => inner.Start();
    public void Stop() => inner.Stop();
}
