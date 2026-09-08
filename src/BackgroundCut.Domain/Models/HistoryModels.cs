namespace BackgroundCut.Domain.Models;

public sealed record ProcessedImageHistoryItem
{
    public ProcessedImageHistoryItem(Guid id, string originalFileName, DateTimeOffset processedAtUtc, string pngPath, string? sourcePath = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A history item must have a non-empty identity.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        if (processedAtUtc == default)
            throw new ArgumentException("A history item must have a processed timestamp.", nameof(processedAtUtc));
        if (sourcePath is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        Id = id;
        OriginalFileName = originalFileName;
        ProcessedAtUtc = processedAtUtc.ToUniversalTime();
        PngPath = pngPath;
        SourcePath = sourcePath;
    }

    public Guid Id { get; }
    public string OriginalFileName { get; }
    public DateTimeOffset ProcessedAtUtc { get; }
    public string PngPath { get; }

    /// <summary>
    /// The absolute path to the original source image at the time it was processed, when known.
    /// Display-only metadata: may be null (older history entries predate this field, or the
    /// source was unavailable), and the file it names may no longer exist. Never used to locate
    /// or validate anything inside the history store.
    /// </summary>
    public string? SourcePath { get; }
}
