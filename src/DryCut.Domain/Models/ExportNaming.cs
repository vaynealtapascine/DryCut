namespace DryCut.Domain.Models;

public static class ExportNaming
{
    public static string CreateCandidate(string sourceFileName, int suffix = 0)
    {
        if (string.IsNullOrWhiteSpace(sourceFileName))
            throw new ArgumentException("A source file name is required.", nameof(sourceFileName));
        ArgumentOutOfRangeException.ThrowIfNegative(suffix);

        var stem = System.IO.Path.GetFileNameWithoutExtension(sourceFileName);
        var baseName = $"{stem}-background-removed";
        return suffix == 0 ? $"{baseName}.png" : $"{baseName} ({suffix + 1}).png";
    }

    public static string CreateUniquePath(string folder, string sourceFileName, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(exists);
        for (var suffix = 0; ; suffix++)
        {
            var candidate = System.IO.Path.Combine(folder, CreateCandidate(sourceFileName, suffix));
            if (!exists(candidate)) return candidate;
        }
    }
}
