using System.Globalization;

namespace Seekers;

/// <summary>
///     Persists the best point of a search to a small human-readable file, so long runs survive interruption. Attached
///     via <see cref="SeekerConfig{TVector, TEval}.Checkpoint"/>; the engine saves on every global improvement
///     (throttled to <see cref="MinInterval"/>) and algorithms force a final save on completion. Improvements are also
///     appended to a history file next to the main file. On the next run, the seeker resumes from the saved values
///     automatically (disable with <see cref="Resume"/>).</summary>
public class SeekerCheckpoint
{
    public string Path { get; }
    /// <summary>Minimum time between saves; improvements arriving faster only update the in-memory pending state.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Whether a newly created seeker loads the saved values as its starting point. Default true.</summary>
    public bool Resume { get; set; } = true;

    private readonly object _lock = new();
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private double[] _pendingValues;
    private string _pendingEval;

    public SeekerCheckpoint(string path)
    {
        Path = path;
    }

    /// <summary>Records a new best. Writes to disk if the throttle allows; otherwise kept pending.</summary>
    public void Save(double[] values, string eval)
    {
        lock (_lock)
        {
            _pendingValues = (double[]) values.Clone();
            _pendingEval = eval;
            if (DateTime.UtcNow - _lastSaveUtc >= MinInterval)
                flush();
        }
    }

    /// <summary>Writes any pending best to disk regardless of the throttle. Called by algorithms on completion.</summary>
    public void SaveFinal()
    {
        lock (_lock)
        {
            if (_pendingValues != null)
                flush();
        }
    }

    private void flush()
    {
        var vals = string.Join(", ", _pendingValues.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        File.WriteAllText(Path, $"eval: {_pendingEval}\r\nvalues: {vals}\r\n");
        File.AppendAllText(historyPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z  eval: {_pendingEval}  values: {vals}\r\n");
        _lastSaveUtc = DateTime.UtcNow;
        _pendingValues = null;
        _pendingEval = null;
    }

    private string historyPath => Path + ".history";

    /// <summary>Loads the saved values, or null if the file is absent or unparseable.</summary>
    public double[] TryLoadValues()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            foreach (var line in File.ReadLines(Path))
                if (line.StartsWith("values:"))
                    return line.Substring("values:".Length)
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
                        .ToArray();
            return null;
        }
        catch
        {
            return null;
        }
    }
}
