using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Pennyworth.Monitoring.Logs;

/// <summary>
/// Critical events, errors and warnings from the System and Application logs, read with
/// <c>wevtutil</c> (part of Windows, no admin rights needed for these two logs).
/// </summary>
public sealed class WindowsEventLogReader : ISystemLogReader
{
    /// <summary>Per log; the newest come first, so a noisy week still shows today.</summary>
    public const int MaxPerLog = 400;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly string[] Logs = ["System", "Application"];

    public string SourceName => "Windows Event Log";

    [SupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<SystemLogEntry>> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var entries = new List<SystemLogEntry>();
        foreach (string log in Logs)
        {
            string xml = await QueryAsync(log, since, cancellationToken);
            entries.AddRange(Parse(xml));
        }

        return entries;
    }

    private static async Task<string> QueryAsync(string log, DateTimeOffset since, CancellationToken cancellationToken)
    {
        long milliseconds = Math.Max((long)(DateTimeOffset.UtcNow - since).TotalMilliseconds, 1);
        var start = new ProcessStartInfo("wevtutil")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Unicode, // "/uni:true" writes UTF-16, whatever the console code page.
        };
        foreach (string argument in new[]
        {
            "qe", log,
            $"/q:*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]",
            "/f:RenderedXml", "/rd:true", $"/c:{MaxPerLog}", "/uni:true", "/e:Events",
        })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("wevtutil did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }

        return await output;
    }

    /// <summary>Parses <c>wevtutil qe /f:RenderedXml /e:Events</c>; damaged events are skipped.</summary>
    public static List<SystemLogEntry> Parse(string xml)
    {
        var entries = new List<SystemLogEntry>();
        XDocument document;
        try
        {
            document = XDocument.Parse(xml.TrimStart('﻿'));
        }
        catch (XmlException)
        {
            return entries;
        }

        foreach (XElement e in document.Root?.Elements(Ns + "Event") ?? [])
        {
            XElement? system = e.Element(Ns + "System");
            string? time = system?.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (system is null || !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at))
            {
                continue;
            }

            LogSeverity severity = system.Element(Ns + "Level")?.Value switch
            {
                "1" => LogSeverity.Critical,
                "2" => LogSeverity.Error,
                _ => LogSeverity.Warning,
            };
            XElement? provider = system.Element(Ns + "Provider");
            string source = provider?.Attribute("EventSourceName")?.Value ?? provider?.Attribute("Name")?.Value ?? "?";
            string? id = system.Element(Ns + "EventID")?.Value;
            string log = system.Element(Ns + "Channel")?.Value ?? "";

            // Without the publisher's message table there is no rendered text: the raw data still helps.
            string? message = e.Element(Ns + "RenderingInfo")?.Element(Ns + "Message")?.Value;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = string.Join(" · ", (e.Element(Ns + "EventData")?.Elements(Ns + "Data") ?? []).Select(d => d.Value).Where(v => v.Length > 0));
            }

            entries.Add(new SystemLogEntry(at, severity, source, id, message, log));
        }

        return entries;
    }
}
