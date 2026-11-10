using System.Text.RegularExpressions;

namespace Pennyworth.Monitoring.Logs;

public enum LogSeverity
{
    Warning,
    Error,
    Critical,
}

/// <summary>One entry from the operating system's own log.</summary>
/// <param name="Source">Who wrote it: "Disk", "Service Control Manager", "kernel".</param>
/// <param name="EventId">Windows' event ID; null on Linux.</param>
/// <param name="Log">"System", "Application", "journal".</param>
public sealed record SystemLogEntry(DateTimeOffset At, LogSeverity Severity, string Source, string? EventId, string Message, string Log);

/// <summary>The same problem repeated: one line for the screen and the assistant instead of hundreds.</summary>
/// <param name="Message">The most recent wording, shortened.</param>
public sealed record LogGroup(string Source, string? EventId, LogSeverity Severity, int Count, DateTimeOffset First, DateTimeOffset Last, string Message, string Log);

/// <summary>Reads warnings and errors from the operating system's log, without admin rights.</summary>
public interface ISystemLogReader
{
    /// <summary>"Windows Event Log" or "systemd journal", for the screen.</summary>
    string SourceName { get; }

    Task<IReadOnlyList<SystemLogEntry>> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>Groups and shortens log entries so the important ones fit in a small context.</summary>
public static partial class LogDigest
{
    public const int MaxMessage = 240;

    /// <summary>Most severe first, then most frequent, then most recent.</summary>
    public static List<LogGroup> Group(IEnumerable<SystemLogEntry> entries, int max = 15) =>
        [.. entries
            .GroupBy(e => (e.Source, e.EventId, Key: e.EventId is null ? Normalize(e.Message) : ""))
            .Select(g =>
            {
                SystemLogEntry last = g.MaxBy(e => e.At)!;
                return new LogGroup(g.Key.Source, g.Key.EventId, g.Max(e => e.Severity), g.Count(), g.Min(e => e.At), last.At, Shorten(last.Message), last.Log);
            })
            .OrderByDescending(g => g.Severity)
            .ThenByDescending(g => g.Count)
            .ThenByDescending(g => g.Last)
            .Take(max)];

    /// <summary>One line, no account SIDs, at most <see cref="MaxMessage"/> characters.</summary>
    public static string Shorten(string message)
    {
        string text = Whitespace().Replace(message, " ").Trim();
        text = Sid().Replace(text, "<sid>");
        return text.Length <= MaxMessage ? text : text[..(MaxMessage - 1)].TrimEnd() + "…";
    }

    /// <summary>Linux messages carry no ID: numbers (PIDs, addresses, counts) are blanked to group repeats.</summary>
    private static string Normalize(string message) => Digits().Replace(message, "#");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Windows account SIDs identify the person; they say nothing about the problem.</summary>
    [GeneratedRegex(@"S-1-5-21(-\d+){3,4}")]
    private static partial Regex Sid();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
