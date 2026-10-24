namespace Pennyworth.Core.Storage;

public interface IPathClassifier
{
    /// <summary>
    /// Returns the category for a directory, or for a file directly in the drive root.
    /// Returns <paramref name="inherited"/> (the parent's category) when no rule matches.
    /// </summary>
    StorageCategory Classify(string fullPath, StorageCategory inherited);
}
