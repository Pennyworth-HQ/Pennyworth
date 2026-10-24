using System.Globalization;
using Pennyworth.Insights.Classification;

namespace Pennyworth.App.Services;

/// <summary>Plain-language titles and descriptions for known folders.</summary>
public interface ILocationInfo
{
    (string Title, string Description)? Describe(string fullPath);
}

public sealed class KnowledgeBaseLocationInfo(RuleClassifier classifier) : ILocationInfo
{
    public (string Title, string Description)? Describe(string fullPath)
    {
        var location = classifier.FindLocation(fullPath);
        if (location is null)
        {
            return null;
        }

        CultureInfo culture = CultureInfo.CurrentUICulture;
        return (location.Title.Get(culture), location.Description.Get(culture));
    }
}
