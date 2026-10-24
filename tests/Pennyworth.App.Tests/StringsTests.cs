using System.Collections;
using System.Globalization;
using System.Resources;
using Pennyworth.App.Resources;

namespace Pennyworth.App.Tests;

public class StringsTests
{
    [Fact]
    public void Every_string_has_a_portuguese_translation()
    {
        var manager = Strings.ResourceManager;
        var english = Keys(manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!);
        var portuguese = Keys(manager.GetResourceSet(CultureInfo.GetCultureInfo("pt-BR"), createIfNotExists: true, tryParents: false)!);

        Assert.Empty(english.Except(portuguese));
        Assert.Empty(portuguese.Except(english));
    }

    [Fact]
    public void Formatted_strings_accept_their_arguments()
    {
        foreach (string culture in new[] { "en-US", "pt-BR" })
        {
            var info = CultureInfo.GetCultureInfo(culture);
            _ = string.Format(info, Strings.ResourceManager.GetString(nameof(Strings.DriveUsed), info)!, "1 GB", "2 GB");
            _ = string.Format(info, Strings.ResourceManager.GetString(nameof(Strings.ScanningFiles), info)!, 1234L);
            _ = string.Format(info, Strings.ResourceManager.GetString(nameof(Strings.ScanComplete), info)!, 1234L);
            _ = string.Format(info, Strings.ResourceManager.GetString(nameof(Strings.DriveFree), info)!, "1 GB");
            _ = string.Format(info, Strings.ResourceManager.GetString(nameof(Strings.PlacesIn), info)!, "Games");
        }
    }

    private static HashSet<string> Keys(ResourceSet set) =>
        set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet();
}
