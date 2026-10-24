using System.Globalization;

namespace Pennyworth.Core.Formatting;

/// <summary>Formats byte counts the way Windows does (1 KB = 1024 bytes).</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        double value = Math.Max(bytes, 0);
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, provider)} {Units[unit]}";
    }
}
