using Xunit;

using BackgroundCut.Application.Ports;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Domain.Models;
using BackgroundCut.Desktop.Ui;

namespace BackgroundCut.Desktop.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void UnsupportedDropEntersErrorStateWithoutStartingEngine()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(new RemoveBackgroundUseCase(engine, new Catalog()), new ExportImageUseCase(new Exporter(), new Settings()), new Clipboard(), new Catalog(), new Desktop());
        vm.DropPath("C:/images/photo.gif");
        Assert.True(vm.HasError);
        Assert.False(engine.Started);
        Assert.Contains("isn't supported", vm.Status);
    }

    [Fact]
    public void DefaultsUseApproachableQualityCopy()
    {
        var vm = new MainViewModel(new RemoveBackgroundUseCase(new FakeEngine(), new Catalog()), new ExportImageUseCase(new Exporter(), new Settings()), new Clipboard(), new Catalog(), new Desktop());
        Assert.Equal(ModelKind.FastAndAccurate, vm.Model);
        Assert.Equal(RefinementPreset.Balanced, vm.Refinement);
        Assert.Contains("recommended", vm.ModelDescription);
        Assert.Contains("balanced", vm.RefinementDescription);
    }

    private sealed class Catalog : IModelCatalog { public ModelDescriptor Resolve(ModelKind k) => new(k, k.ToString(), k.ToString(), true, true); }
    private sealed class FakeEngine : IBackgroundRemovalEngine { public bool Started; public Task<ProcessedImage> ProcessAsync(ImageInput i, ModelDescriptor m, RefinementSettings r, IProgress<ProcessingProgress>? p, CancellationToken c) { Started = true; return Task.FromResult(new ProcessedImage(new byte[4], 1, 1)); } }
    private sealed class Exporter : IExportService { public Task<ExportedFile> ExportAsync(ProcessedImage i, ExportRequest r, CancellationToken c) => Task.FromResult(new ExportedFile("out.png")); }
    private sealed class Settings : ISettingsStore { public Task<ExportSettings> LoadExportSettingsAsync(CancellationToken c) => Task.FromResult(ExportSettings.Default); public Task SaveExportSettingsAsync(ExportSettings s, CancellationToken c) => Task.CompletedTask; }
    private sealed class Clipboard : IClipboardService { public Task CopyAsync(ProcessedImage i, CancellationToken c) => Task.CompletedTask; }
    private sealed class Desktop : IDesktopServices { public IFileDialogService FileDialogs => throw new NotImplementedException(); public IPreviewBitmapFactory Preview => throw new NotImplementedException(); public Task OpenSettingsAsync(System.Windows.Window o) => Task.CompletedTask; public void ShowMessage(System.Windows.Window o, string m, string t) { } }
}
