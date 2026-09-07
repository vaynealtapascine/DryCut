using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Application.UseCases;

public sealed class RemoveBackgroundUseCase
{
    private readonly IBackgroundRemovalEngine _engine;
    private readonly IModelCatalog _models;

    public RemoveBackgroundUseCase(IBackgroundRemovalEngine engine, IModelCatalog models)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    public Task<ProcessedImage> ExecuteAsync(ImageInput input, ProcessingOptions? options = null, IProgress<ProcessingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.IsSupportedLocalFile)
            throw new ArgumentException("The input must be a fully qualified supported image file.", nameof(input));

        var effectiveOptions = options ?? new ProcessingOptions();
        var model = _models.Resolve(effectiveOptions.Model);
        if (!model.IsInstalled)
            throw new InvalidOperationException($"The selected model is not installed: {model.DisplayName}.");

        return _engine.ProcessAsync(input, model, effectiveOptions.EffectiveRefinement, progress, cancellationToken);
    }
}

public sealed class ExportImageUseCase
{
    private readonly IExportService _export;
    private readonly ISettingsStore _settings;

    public ExportImageUseCase(IExportService export, ISettingsStore settings)
    {
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<ExportedFile> ExecuteAsync(
        ProcessedImage image,
        string? sourceFolder = null,
        string? requestedPath = null,
        string? suggestedFileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var settings = await _settings.LoadExportSettingsAsync(cancellationToken).ConfigureAwait(false);
        var policy = requestedPath is null ? settings.Policy : ExportPolicy.AskEveryTime;
        var request = new ExportRequest(policy, settings.DefaultFolder, sourceFolder, requestedPath, suggestedFileName);
        return await _export.ExportAsync(image, request, cancellationToken).ConfigureAwait(false);
    }
}
