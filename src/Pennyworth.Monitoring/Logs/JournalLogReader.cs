using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Pennyworth.Monitoring.Logs;

/// <summary>
/// Warnings and worse from the systemd journal, read with <c>journalctl</c>. A normal user sees
/// the system journal when in the <c>systemd-journal</c> or <c>adm</c> group, otherwise only
/// their own entries. Waits for <c>Pennyworth.Platform.Linux</c> to be picked up.
/// </summary>
public sealed class JournalLogReader : ISystemLogReader
{
    public const int MaxEntries = 800;

    public string SourceName => "systemd journal";

    [SupportedOSPlatform("linux")]
    public async Task<IReadOnlyList<SystemLogEntry>> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("journalctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[]
        {
            "--no-pager", "--quiet", "--output=json", "--priority=warning", "--reverse",
            $"--since=@{since.ToUnixTimeSeconds()}", $"--lines={MaxEntries}",
        })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("journalctl did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return Parse(await output);
    }

    /// <summary>Parses <c>journalctl --output=json</c>: one JSON object per line.</summary>
    public static List<SystemLogEntry> Parse(string json)
    {
        var entries = new List<SystemLogEntry>();
        foreach (string line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!long.TryParse(Text(root, "__REALTIME_TIMESTAMP"), out long microseconds))
                {
                    continue;
                }

                LogSeverity severity = Text(root, "PRIORITY") switch
                {
                    "0" or "1" or "2" => LogSeverity.Critical,
                    "3" => LogSeverity.Error,
                    _ => LogSeverity.Warning,
                };
                string source = Text(root, "SYSLOG_IDENTIFIER") ?? Text(root, "_COMM") ?? Text(root, "_SYSTEMD_UNIT") ?? "?";
                entries.Add(new SystemLogEntry(
                    DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000),
                    severity,
                    source,
                    null,
                    Text(root, "MESSAGE") ?? "",
                    "journal"));
            }
            catch (JsonException)
            {
                // A cut line at the end of the output.
            }
        }

        return entries;
    }

    /// <summary>A field as text; the journal sends non-UTF-8 values as an array of bytes.</summary>
    private static string? Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => Encoding.UTF8.GetString([.. value.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Number).Select(b => (byte)b.GetInt32())]),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
