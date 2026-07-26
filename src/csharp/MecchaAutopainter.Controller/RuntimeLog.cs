using MecchaCamouflage.Core;

namespace MecchaCamouflage.Controller;

public sealed class RuntimeLog
{
    private readonly AppPaths paths;
    private readonly object gate = new();
    private readonly List<string> lines = [];

    public RuntimeLog(AppPaths paths)
    {
        this.paths = paths;
        Directory.CreateDirectory(paths.LogDirectory);
    }

    public event EventHandler? Changed;

    public string Text
    {
        get
        {
            lock (gate)
                return lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines);
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        var path = Path.Combine(paths.LogDirectory, $"runtime-{DateTime.Now:yyyy-MM-dd}.log");
        lock (gate)
        {
            lines.Add(line);
            while (lines.Count > 400)
                lines.RemoveAt(0);
        }
        TryAppendLine(path, line);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TryAppendLine(string path, string line)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Runtime logging is best-effort. The in-memory log remains the UI source.
        }
    }
}
