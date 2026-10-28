using System.Text.RegularExpressions;

namespace Pennyworth.Monitoring;

/// <summary>
/// A rough processor power figure for PCs whose Windows doesn't measure it: the processor's
/// rated power (TDP), guessed from the model suffix in its name, scaled by the load. It shows the
/// trend, not a measurement: real draw can run above TDP for a while under heavy load.
/// </summary>
public static partial class PowerEstimate
{
    /// <summary>An idle processor still draws about a tenth of its rated power.</summary>
    private const double IdleShare = 0.1;

    /// <summary>Watts at <paramref name="cpuPercent"/> load (0 to 100) for a processor rated at <paramref name="tdpWatts"/>.</summary>
    public static double Watts(double tdpWatts, double cpuPercent) =>
        tdpWatts * (IdleShare + ((1 - IdleShare) * Math.Clamp(cpuPercent / 100, 0, 1)));

    /// <summary>
    /// The rated power of the usual parts in a family, from the processor name ("Intel(R) Core(TM)
    /// i7-6700T CPU @ 2.80GHz" → 35 W). Null for names it doesn't recognise.
    /// </summary>
    public static double? TdpWatts(string processorName)
    {
        string name = Regex.Replace(processorName, @"\((R|TM)\)", "", RegexOptions.IgnoreCase);

        if (IntelCore().Match(name) is { Success: true } core)
        {
            int model = int.Parse(core.Groups["model"].Value, System.Globalization.CultureInfo.InvariantCulture);
            return Intel(core.Groups["suffix"].Value.ToUpperInvariant(), model);
        }

        if (IntelUltra().Match(name) is { Success: true } ultra)
        {
            return ultra.Groups["suffix"].Value.ToUpperInvariant() switch
            {
                "U" => 15,
                "V" => 17,
                "H" => 28,
                "HX" => 55,
                "K" or "KF" => 125,
                _ => 65,
            };
        }

        if (Ryzen().Match(name) is { Success: true } ryzen)
        {
            return ryzen.Groups["suffix"].Value.ToUpperInvariant() switch
            {
                "U" => 15,
                "HS" or "GE" or "E" => 35,
                "H" => 45,
                "HX" => 55,
                "X3D" => 120,
                "X" or "XT" => 105,
                _ => 65,
            };
        }

        // Pentium, Celeron and Atom mobile parts: N5105, J4125, N100.
        if (LowPower().IsMatch(name))
        {
            return 10;
        }

        return null;
    }

    /// <param name="model">"6700" or "12900": the generation decides the K-series rating.</param>
    private static double Intel(string suffix, int model)
    {
        bool tenthOrLater = model >= 10000;
        return suffix switch
        {
            "T" or "TE" => 35,
            "K" or "KF" or "KS" => tenthOrLater ? 125 : 91,
            "U" => 15,
            "Y" => 7,
            "P" => 28,
            "H" or "HK" or "HQ" => 45,
            "HX" => 55,
            // Ice Lake and Tiger Lake laptops: i7-1065G7, i5-1135G7.
            _ when suffix.StartsWith('G') && suffix.Length == 2 => 15,
            _ => 65,
        };
    }

    [GeneratedRegex(@"\bi[3579]-(?<model>\d{4,5})(?<suffix>[A-Z]{0,2}\d?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCore();

    [GeneratedRegex(@"\bUltra [3579] \d{3}(?<suffix>[A-Z]{0,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelUltra();

    [GeneratedRegex(@"\bRyzen (?:AI )?\d+ (?:PRO )?\d{3,4}(?<suffix>[A-Z0-9]{0,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Ryzen();

    [GeneratedRegex(@"\b[NJ]\d{3,4}\b")]
    private static partial Regex LowPower();
}
