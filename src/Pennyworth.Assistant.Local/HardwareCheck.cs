using System.Globalization;
using Pennyworth.Core.Formatting;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Assistant.Local;

public enum HardwareFit
{
    /// <summary>Should run well.</summary>
    Good,

    /// <summary>Will work, slowly.</summary>
    Slow,

    /// <summary>Likely to struggle: little memory.</summary>
    NotEnoughMemory,

    /// <summary>The download doesn't fit on the drive.</summary>
    NotEnoughSpace,
}

/// <summary>A plain verdict with its reasons. The person can always continue (spec §10).</summary>
public sealed record HardwareVerdict(LocalModel Model, HardwareFit Fit, IReadOnlyList<string> Reasons);

/// <summary>Picks the model that suits this PC and says, before any download, how it will run.</summary>
public static class HardwareCheck
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>Room to leave on the drive after the download.</summary>
    public const long SpareDiskBytes = 2 * Gb;

    /// <summary>The graphics-card model when the PC has a card with enough video memory, otherwise the processor model.</summary>
    public static LocalModel Recommend(ModelCatalog catalog, PcSpecs? specs)
    {
        LocalModel? gpu = catalog.Models.FirstOrDefault(m => m.Device == ModelDevice.Gpu);
        return gpu is not null && VideoMemory(specs) >= gpu.MinVideoMemoryGb * Gb
            ? gpu
            : catalog.Models.First(m => m.Device == ModelDevice.Cpu);
    }

    public static HardwareVerdict Evaluate(LocalModel model, PcSpecs? specs, long availableRamBytes, long freeDiskBytes, CultureInfo culture)
    {
        bool pt = culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        string T(string en, string ptBr) => pt ? ptBr : en;
        var reasons = new List<string>();
        HardwareFit fit = HardwareFit.Good;
        void Worse(HardwareFit to)
        {
            if (to > fit)
            {
                fit = to;
            }
        }

        if (freeDiskBytes < model.Size + SpareDiskBytes)
        {
            Worse(HardwareFit.NotEnoughSpace);
            reasons.Add(T(
                $"The download needs {ByteSize.Format(model.Size + SpareDiskBytes, culture)} free; the drive has {ByteSize.Format(freeDiskBytes, culture)}.",
                $"O download precisa de {ByteSize.Format(model.Size + SpareDiskBytes, culture)} livres; o disco tem {ByteSize.Format(freeDiskBytes, culture)}."));
        }

        long totalRam = specs?.Memory.Total ?? 0;
        if (totalRam > 0 && totalRam < model.MinRamGb * Gb * 0.95)
        {
            Worse(HardwareFit.NotEnoughMemory);
            reasons.Add(T(
                $"This PC has {ByteSize.Format(totalRam, culture)} of memory; {model.MinRamGb} GB or more is recommended.",
                $"Este PC tem {ByteSize.Format(totalRam, culture)} de memória; o recomendado é {model.MinRamGb} GB ou mais."));
        }
        else if (availableRamBytes > 0 && availableRamBytes < model.Size * 1.2)
        {
            Worse(HardwareFit.Slow);
            reasons.Add(T(
                "Memory is busy right now; closing a few apps before asking will help.",
                "A memória está ocupada agora; fechar alguns apps antes de perguntar vai ajudar."));
        }

        if (model.Device == ModelDevice.Gpu && VideoMemory(specs) < model.MinVideoMemoryGb * Gb)
        {
            Worse(HardwareFit.NotEnoughMemory);
            reasons.Add(T(
                $"This model needs a graphics card with {model.MinVideoMemoryGb} GB of video memory.",
                $"Este modelo precisa de uma placa de vídeo com {model.MinVideoMemoryGb} GB de memória de vídeo."));
        }

        if (model.Device == ModelDevice.Cpu)
        {
            Worse(HardwareFit.Slow);
            reasons.Add(model.Note.Get(culture));
        }

        if (reasons.Count == 0)
        {
            reasons.Add(model.Note.Get(culture));
        }

        return new HardwareVerdict(model, fit, reasons);
    }

    /// <summary>Dedicated video memory of the best real graphics card (not the basic display adapter).</summary>
    public static long VideoMemory(PcSpecs? specs) =>
        specs?.Gpus.Where(g => !g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)).Max(g => g.Memory) ?? 0;
}
