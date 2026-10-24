using Pennyworth.KnowledgeBase;

namespace Pennyworth.Monitoring;

/// <summary>One row of the Memory &amp; CPU list: all processes of one app.</summary>
/// <param name="Explanation">Set for processes the knowledge base explains ("Windows services").</param>
/// <param name="Description">The executable's own product description ("Google Chrome"), if any.</param>
/// <param name="Company">The executable's publisher ("Google LLC"), if any.</param>
public sealed record AppGroup(
    string Key,
    ProcessExplanation? Explanation,
    string? Description,
    string? Company,
    string? ImagePath,
    long Memory,
    double Cpu,
    IReadOnlyList<AppProcess> Processes)
{
    public bool IsProtected => Explanation?.IsProtected ?? false;

    public bool CanClose => Explanation?.CanClose ?? true;
}

/// <param name="Services">Windows services hosted by this process, for "Windows services".</param>
public sealed record AppProcess(int Pid, string Name, long Memory, double Cpu, IReadOnlyList<string> Services);

/// <summary>Where the grouper gets details Windows only gives per process.</summary>
public interface IProcessDetails
{
    string? GetImagePath(int pid);

    (string? Description, string? Company) GetVersionInfo(string imagePath);

    IReadOnlyDictionary<int, List<string>> GetServicesByPid();
}

/// <summary>
/// Groups processes by app: explained Windows processes by their knowledge-base entry,
/// everything else by executable name (30 "chrome" processes → one Google Chrome row).
/// </summary>
public sealed class AppGrouper(IEnumerable<ProcessExplanation> explanations, IProcessDetails details)
{
    private readonly Dictionary<string, ProcessExplanation> _byName = explanations
        .SelectMany(e => e.Names.Select(name => (name, e)))
        .ToDictionary(pair => pair.name, pair => pair.e, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AppGroup> Group(IReadOnlyList<ProcessSample> processes, IReadOnlyDictionary<int, double> cpu)
    {
        IReadOnlyDictionary<int, List<string>>? services = null;
        var groups = new List<AppGroup>();
        foreach (var group in processes.GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase))
        {
            ProcessExplanation? explanation = _byName.GetValueOrDefault(group.First().Name);
            if (explanation?.ListServices == true)
            {
                services ??= details.GetServicesByPid();
            }

            var members = group
                .Select(p => new AppProcess(
                    p.Pid,
                    p.Name,
                    MemoryOf(p, explanation),
                    cpu.GetValueOrDefault(p.Pid),
                    services?.GetValueOrDefault(p.Pid) ?? (IReadOnlyList<string>)[]))
                .OrderByDescending(p => p.Memory)
                .ToArray();

            string? path = explanation is null ? group.Select(p => details.GetImagePath(p.Pid)).FirstOrDefault(p => p is not null) : null;
            var (description, company) = path is null ? (null, null) : details.GetVersionInfo(path);
            groups.Add(new AppGroup(
                group.Key,
                explanation,
                string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                string.IsNullOrWhiteSpace(company) ? null : company.Trim(),
                path,
                members.Sum(p => p.Memory),
                members.Sum(p => p.Cpu),
                members));
        }

        return groups.OrderByDescending(g => g.Memory).ToArray();
    }

    private string KeyOf(ProcessSample process) =>
        _byName.TryGetValue(process.Name, out var explanation) ? "kb:" + explanation.Id : process.Name;

    // The compressed store lives in the working set of "Memory Compression", not in private memory.
    private static long MemoryOf(ProcessSample process, ProcessExplanation? explanation) =>
        explanation?.Id == "memory-compression" ? process.WorkingSet : process.PrivateMemory;
}
