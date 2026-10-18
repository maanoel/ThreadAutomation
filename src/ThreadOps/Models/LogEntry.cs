namespace ThreadOps.Models;

public enum LogLevel { Info, Ok, Warn, Action, Error }

public sealed record LogEntry(DateTime Time, string Message, LogLevel Level);
