namespace Seekers;

public enum SeekerLogLevel
{
    /// <summary>No output at all.</summary>
    Off = 0,
    /// <summary>Only new-best announcements.</summary>
    Improvements = 1,
    /// <summary>Improvements plus per-iteration / per-direction progress (restarts, direction outcomes).</summary>
    Iterations = 2,
    /// <summary>Everything, including every step of every line search. Very wordy.</summary>
    Steps = 3,
}

/// <summary>
///     Logging for Seekers algorithms. Destination-agnostic: a plain <see cref="Sink"/> delegate, defaulting to
///     Console. Verbosity is a single level per logger, but loggers are cheap to fork: <see cref="Sub"/> creates a
///     child logger (same sink, prefixed) for a sub-algorithm, letting the caller decide how loud each nested part is.
///     A nested search configured with its own <see cref="SeekerConfig{TVector, TEval}.Log"/> is fully independent.
///     All log calls are guarded so that message strings are never built when the level is off — check the
///     <see cref="WantImprovements"/>-style properties before doing any expensive formatting of your own.</summary>
public class SeekerLog
{
    /// <summary>Where log lines go. Null means fully off regardless of level.</summary>
    public Action<string> Sink { get; set; } = System.Console.WriteLine;
    public SeekerLogLevel Level { get; set; } = SeekerLogLevel.Off;
    /// <summary>Prepended to every line; sub-loggers accumulate prefixes.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>A logger that outputs nothing. Shared instance; do not mutate.</summary>
    public static SeekerLog None { get; } = new SeekerLog { Sink = null, Level = SeekerLogLevel.Off };

    /// <summary>Creates a console logger at the given level.</summary>
    public static SeekerLog Console(SeekerLogLevel level = SeekerLogLevel.Improvements) => new SeekerLog { Level = level };

    /// <summary>Creates a logger with a custom sink at the given level.</summary>
    public static SeekerLog To(Action<string> sink, SeekerLogLevel level = SeekerLogLevel.Improvements) => new SeekerLog { Sink = sink, Level = level };

    public bool WantImprovements => Sink != null && Level >= SeekerLogLevel.Improvements;
    public bool WantIterations => Sink != null && Level >= SeekerLogLevel.Iterations;
    public bool WantSteps => Sink != null && Level >= SeekerLogLevel.Steps;

    public void Improvement(string msg) { if (WantImprovements) Sink(Prefix + msg); }
    public void Iteration(string msg) { if (WantIterations) Sink(Prefix + msg); }
    public void Step(string msg) { if (WantSteps) Sink(Prefix + msg); }

    /// <summary>
    ///     Creates a child logger for a sub-algorithm: same sink, additional prefix, and optionally a different level.
    ///     Pass <see cref="SeekerLogLevel.Off"/> to silence a wordy sub-algorithm while keeping the parent's output, or
    ///     a higher level to trace only the sub-algorithm.</summary>
    public SeekerLog Sub(string prefix, SeekerLogLevel? level = null) => new SeekerLog { Sink = Sink, Level = level ?? Level, Prefix = Prefix + prefix };

    /// <summary>Formats a value with adaptive precision: more significant digits for smaller magnitudes.</summary>
    public static string Num(double v)
    {
        var a = Math.Abs(v);
        if (a >= 0.1 || v == 0)
            return v.ToString("#,0.#####");
        else if (a >= 0.0001)
            return v.ToString("0.########");
        else
            return v.ToString("e5");
    }

    /// <summary>Formats a vector of parameter values for logging.</summary>
    public static string Vec(IEnumerable<double> values) => string.Join(", ", values.Select(Num));
}
