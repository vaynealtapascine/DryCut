using BackgroundCut.Domain.Models;

namespace BackgroundCut.Application.Ports;

public interface IBackgroundRemovalEngine
{
    Task<ProcessedImage> ProcessAsync(ImageInput input, ModelDescriptor model, RefinementSettings refinement, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken);
}

public interface IModelCatalog
{
    ModelDescriptor Resolve(ModelKind kind);
}

public interface ISettingsStore
{
    Task<ExportSettings> LoadExportSettingsAsync(CancellationToken cancellationToken);
    Task SaveExportSettingsAsync(ExportSettings settings, CancellationToken cancellationToken);
}

public interface IExportService
{
    Task<ExportedFile> ExportAsync(ProcessedImage image, ExportRequest request, CancellationToken cancellationToken);
}

public interface IClipboardService
{
    Task CopyAsync(ProcessedImage image, CancellationToken cancellationToken);
}

public interface IProcessedImageHistoryStore
{
    /// <summary>Saves one processed image and metadata without retaining its source path.</summary>
    Task<ProcessedImageHistoryItem> SaveAsync(
        ProcessedImage image,
        string originalFileName,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Returns metadata only, newest first; no PNG pixels are loaded.</summary>
    Task<IReadOnlyList<ProcessedImageHistoryItem>> EnumerateMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads one stored PNG, or null when its artifact is unavailable or invalid.</summary>
    Task<ProcessedImage?> LoadAsync(ProcessedImageHistoryItem item, CancellationToken cancellationToken = default);

    /// <summary>Resolves the local PNG artifact for preview without decoding it.</summary>
    string? GetImagePath(ProcessedImageHistoryItem item);

    /// <summary>Deletes one item and its persisted artifacts. Missing artifacts are ignored.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Removes visible items strictly older than the retention boundary and stale temporary files.</summary>
    /// <remarks>Unpaired PNGs are preserved because a user may have manually exported one into the history folder.</remarks>
    Task<int> CleanupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public static class ProcessedImageHistoryPolicy
{
    public const int GalleryPreviewLimit = 100;
    public static TimeSpan Retention => TimeSpan.FromDays(30);
}

public interface IExplorerIntegration
{
    bool IsSupported { get; }
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);
    Task EnableAsync(CancellationToken cancellationToken);
    Task DisableAsync(CancellationToken cancellationToken);
}

public sealed record ProcessingProgress(string Stage, double Fraction);
public sealed record ExportRequest(
    ExportPolicy Policy,
    string? DefaultFolder,
    string? SourceFolder,
    string? RequestedPath = null,
    string? SuggestedFileName = null);
public sealed record ExportedFile(string Path);
