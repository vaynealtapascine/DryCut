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

public interface IExplorerIntegration
{
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
