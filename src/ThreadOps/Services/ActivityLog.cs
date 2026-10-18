using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ThreadOps.Models;

namespace ThreadOps.Services;

/// <summary>Log exibido no painel e gravado em logs/AAAA-MM-DD.log ao lado do executável.</summary>
public sealed class ActivityLog
{
    private const int MaxVisibleEntries = 500;

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public string FilePath { get; }

    public ActivityLog()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");
    }

    public void Info(string message) => Add(message, LogLevel.Info);
    public void Ok(string message) => Add(message, LogLevel.Ok);
    public void Warn(string message) => Add(message, LogLevel.Warn);
    public void Action(string message) => Add(message, LogLevel.Action);
    public void Error(string message) => Add(message, LogLevel.Error);

    private void Add(string message, LogLevel level)
    {
        var entry = new LogEntry(DateTime.Now, message, level);
        Application.Current.Dispatcher.Invoke(() =>
        {
            Entries.Add(entry);
            if (Entries.Count > MaxVisibleEntries) Entries.RemoveAt(0);
        });

        try
        {
            File.AppendAllText(FilePath, $"{entry.Time:HH:mm:ss} [{level.ToString().ToUpperInvariant()}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // O log em arquivo é secundário; não derruba o app.
        }
    }
}
