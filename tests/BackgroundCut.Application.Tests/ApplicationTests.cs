using BackgroundCut.Application.Ports;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Application.Tests;

public sealed class ApplicationTests
{
    [Fact]
    public async Task RemoveBackgroundResolvesModelAndForwardsOptions()
    {
        var catalog = new FakeCatalog();
        var engine = new FakeEngine();
        var useCase = new RemoveBackgroundUseCase(engine, catalog);
        var input = new ImageInput("C:/images/photo.png");
        var options = new ProcessingOptions(ModelKind.HighestQuality, new RefinementSettings(RefinementPreset.Detailed));

        var result = await useCase.ExecuteAsync(input, options);

        Assert.Same(engine.Result, result);
        Assert.Equal(ModelKind.HighestQuality, engine.Model!.Kind);
        Assert.Equal(RefinementPreset.Detailed, engine.Refinement!.Preset);
        Assert.Equal(input, engine.Input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("photo.jpg")]
    [InlineData("C:/images/photo.gif")]
    public async Task RemoveBackgroundRejectsInvalidInput(string path)
    {
        var useCase = new RemoveBackgroundUseCase(new FakeEngine(), new FakeCatalog());
        await Assert.ThrowsAsync<ArgumentException>(() => useCase.ExecuteAsync(new ImageInput(path)));
    }

    [Fact]
    public async Task ExportImageLoadsSettingsAndForwardsAllExportContext()
    {
        var exporter = new FakeExporter();
        var settings = new FakeSettings(new ExportSettings(ExportPolicy.SourceFolder, "C:/default"));
        var useCase = new ExportImageUseCase(exporter, settings);

        var image = new ProcessedImage(new byte[] { 1 }, 1, 1);
        var result = await useCase.ExecuteAsync(image, "C:/source", cancellationToken: CancellationToken.None);

        Assert.Equal("C:/source/photo.png", result.Path);
        Assert.Equal(ExportPolicy.SourceFolder, exporter.Request!.Policy);
        Assert.Equal("C:/default", exporter.Request.DefaultFolder);
        Assert.Equal("C:/source", exporter.Request.SourceFolder);
    }

    private sealed class FakeCatalog : IModelCatalog
    {
        public ModelDescriptor Resolve(ModelKind kind) => new(kind, kind.ToString(), kind.ToString(), kind == ModelKind.FastAndAccurate, true);
    }

    private sealed class FakeEngine : IBackgroundRemovalEngine
    {
        public ImageInput? Input { get; private set; }
        public ModelDescriptor? Model { get; private set; }
        public RefinementSettings? Refinement { get; private set; }
        public ProcessedImage Result { get; } = new(new byte[] { 7 }, 2, 2);
        public Task<ProcessedImage> ProcessAsync(ImageInput input, ModelDescriptor model, RefinementSettings refinement, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
        { Input = input; Model = model; Refinement = refinement; return Task.FromResult(Result); }
    }

    private sealed class FakeSettings(ExportSettings value) : ISettingsStore
    {
        public Task<ExportSettings> LoadExportSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(value);
        public Task SaveExportSettingsAsync(ExportSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeExporter : IExportService
    {
        public ExportRequest? Request { get; private set; }
        public Task<ExportedFile> ExportAsync(ProcessedImage image, ExportRequest request, CancellationToken cancellationToken)
        { Request = request; return Task.FromResult(new ExportedFile("C:/source/photo.png")); }
    }
}
