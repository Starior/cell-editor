using System.IO;

namespace CellEditor;

public sealed class LogLine
{
    public required long Id { get; init; }

    public required DateTimeOffset Time { get; init; }

    public required string Level { get; init; }

    public required string Message { get; init; }

    public string Display => Time.ToLocalTime().ToString("HH:mm:ss") + "  " + Message;
}

/// <summary>
/// Session log. The window shows it, the local agent server reads the same lines,
/// and every line is also appended to one file per session.
/// </summary>
public sealed class EditorLog
{
    const int KeepSessions = 30;

    readonly object gate = new();
    readonly List<LogLine> lines = new();
    readonly string? file;
    long nextId = 1;

    public EditorLog()
    {
        try
        {
            Directory.CreateDirectory(AppHome.Logs);
            file = Path.Combine(AppHome.Logs, "session-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
            foreach (var old in Directory.GetFiles(AppHome.Logs, "session-*.log").OrderByDescending(path => path).Skip(KeepSessions - 1))
            {
                File.Delete(old);
            }
        }
        catch
        {
            file = null;
        }
    }

    public string? FilePath => file;

    public event Action<LogLine>? Added;

    /// <summary>Written to the session file only, for long lists that would flood the window.</summary>
    public void Detail(string message)
    {
        Append(DateTimeOffset.Now, "detail", message);
    }

    void Append(DateTimeOffset time, string level, string message)
    {
        if (file == null)
        {
            return;
        }

        try
        {
            lock (gate)
            {
                File.AppendAllText(file, time.ToLocalTime().ToString("HH:mm:ss.fff") + " " + level + "  " + message + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    public void Info(string message)
    {
        Write("info", message);
    }

    public void Error(string message)
    {
        Write("error", message);
    }

    public IReadOnlyList<LogLine> After(long afterId)
    {
        lock (gate)
        {
            return lines.Where(line => line.Id > afterId).ToArray();
        }
    }

    public string AllText()
    {
        lock (gate)
        {
            return string.Join(Environment.NewLine, lines.Select(line => line.Display));
        }
    }

    void Write(string level, string message)
    {
        LogLine line;
        lock (gate)
        {
            line = new LogLine
            {
                Id = nextId++,
                Time = DateTimeOffset.Now,
                Level = level,
                Message = message,
            };
            lines.Add(line);
        }

        Append(line.Time, level, message);
        Added?.Invoke(line);
    }
}
