namespace BackgroundCut.Domain.Models;

public sealed record ProcessedImageHistoryItem
{
    public ProcessedImageHistoryItem(Guid id, string originalFileName, DateTimeOffset processedAtUtc, string pngPath)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A history item must have a non-empty identity.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        if (processedAtUtc == default)
            throw new ArgumentException("A history item must have a processed timestamp.", nameof(processedAtUtc));

        Id = id;
        OriginalFileName = originalFileName;
        ProcessedAtUtc = processedAtUtc.ToUniversalTime();
        PngPath = pngPath;
    }

    public Guid Id { get; }
    public string OriginalFileName { get; }
    public DateTimeOffset ProcessedAtUtc { get; }
    public string PngPath { get; }
}
