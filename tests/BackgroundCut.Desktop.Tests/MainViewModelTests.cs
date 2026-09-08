using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BackgroundCut.Application.Ports;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Desktop.Ui;
using BackgroundCut.Domain.Models;
using Xunit;

namespace BackgroundCut.Desktop.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void UnsupportedDropEntersErrorStateWithoutStartingEngine()
    {
        var engine = new FakeEngine();
        using var vm = CreateViewModel(engine);

        vm.DropPath("C:/images/photo.gif");

        Assert.True(vm.HasError);
        Assert.False(engine.Started);
        Assert.Contains("isn't supported", vm.Status);
    }

    [Fact]
    public void DefaultsUseApproachableQualityCopy()
    {
        using var vm = CreateViewModel(new FakeEngine());

        Assert.Equal(ModelKind.FastAndAccurate, vm.Model);
        Assert.Equal(RefinementPreset.Balanced, vm.Refinement);
        Assert.Contains("recommended", vm.ModelDescription);
        Assert.Contains("balanced", vm.RefinementDescription);
        Assert.Contains("30 days", vm.RetentionNotice);
    }

    [Fact]
    public async Task MultipleDropsAreProcessedSequentiallyAndPersisted()
    {
        var paths = CreateInputFiles(3);
        var engine = new FakeEngine(TimeSpan.FromMilliseconds(20));
        var history = new HistoryStore();
        using var vm = CreateViewModel(engine, history);

        try
        {
            vm.DropPaths(paths);
            await vm.WhenQueueIsIdleAsync();

            Assert.Equal(paths, engine.ProcessedPaths);
            Assert.Equal(1, engine.MaximumConcurrency);
            Assert.Equal(3, history.Items.Count);
            Assert.Equal(3, vm.TotalItemCount);
            Assert.All(vm.VisibleItems, item => Assert.True(item.IsCompleted));
            Assert.Equal(0, vm.QueueCount);
            Assert.Equal("All caught up", vm.QueueEtaText);
        }
        finally
        {
            DeleteInputFiles(paths);
        }
    }

    [Fact]
    public async Task StartupHistoryDisplaysOnlyFirstHundredItems()
    {
        var history = new HistoryStore();
        for (var index = 0; index < 105; index++)
        {
            var image = new ProcessedImage([(byte)index, 2, 3, 255], 1, 1);
            await history.SaveAsync(image, $"image-{index}.png", DateTimeOffset.UtcNow.AddMinutes(-index));
        }
        using var vm = CreateViewModel(new FakeEngine(), history);

        await vm.InitializeAsync();

        Assert.Equal(105, vm.TotalItemCount);
        Assert.Equal(100, vm.VisibleItems.Count);
        Assert.Equal(5, vm.HiddenItemCount);
        Assert.True(vm.HasHiddenItems);
        Assert.Contains("stored off-screen", vm.GallerySummary);
        Assert.Equal("image-0.png", vm.VisibleItems[0].DisplayName);
    }

    [Fact]
    public async Task GalleryActionsCopyAndDeleteOneItem()
    {
        var history = new HistoryStore();
        await history.SaveAsync(new ProcessedImage([1, 2, 3, 128], 1, 1), "gallery.png", DateTimeOffset.UtcNow);
        var clipboard = new Clipboard();
        using var vm = CreateViewModel(new FakeEngine(), history, clipboard);
        await vm.InitializeAsync();
        var item = Assert.Single(vm.VisibleItems);

        vm.CopyQueueItemCommand.Execute(item);
        Assert.Equal(1, clipboard.CopyCount);

        vm.DeleteQueueItemCommand.Execute(item);
        Assert.Empty(vm.VisibleItems);
        Assert.Empty(history.Items);
    }

    private static MainViewModel CreateViewModel(FakeEngine engine, HistoryStore? history = null, Clipboard? clipboard = null)
    {
        var settings = new Settings();
        return new MainViewModel(
            new RemoveBackgroundUseCase(engine, new Catalog()),
            new ExportImageUseCase(new Exporter(), settings),
            clipboard ?? new Clipboard(),
            settings,
            new Desktop(),
            history);
    }

    private static string[] CreateInputFiles(int count)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BackgroundCut-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Enumerable.Range(1, count).Select(index =>
        {
            var path = Path.Combine(directory, $"image-{index}.png");
            File.WriteAllBytes(path, [1, 2, 3]);
            return path;
        }).ToArray();
    }

    private static void DeleteInputFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var directory = Path.GetDirectoryName(paths[0]);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class Catalog : IModelCatalog
    {
        public ModelDescriptor Resolve(ModelKind kind) => new(kind, kind.ToString(), kind.ToString(), true, true);
    }

    private sealed class FakeEngine(TimeSpan? delay = null) : IBackgroundRemovalEngine
    {
        private int _concurrency;
        public bool Started { get; private set; }
        public int MaximumConcurrency { get; private set; }
        public List<string> ProcessedPaths { get; } = [];

        public async Task<ProcessedImage> ProcessAsync(
            ImageInput input,
            ModelDescriptor model,
            RefinementSettings refinement,
            IProgress<ProcessingProgress>? progress,
            CancellationToken cancellationToken)
        {
            Started = true;
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            try
            {
                ProcessedPaths.Add(input.Path);
                progress?.Report(new ProcessingProgress("Removing the background…", 0.5));
                if (delay is not null) await Task.Delay(delay.Value, cancellationToken);
                return new ProcessedImage([1, 2, 3, 128], 1, 1);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class Exporter : IExportService
    {
        public Task<ExportedFile> ExportAsync(ProcessedImage image, ExportRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ExportedFile("out.png"));
    }

    private sealed class Settings : ISettingsStore
    {
        public Task<ExportSettings> LoadExportSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(ExportSettings.Default);
        public Task SaveExportSettingsAsync(ExportSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Clipboard : IClipboardService
    {
        public int CopyCount { get; private set; }

        public Task CopyAsync(ProcessedImage image, CancellationToken cancellationToken)
        {
            CopyCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class Desktop : IDesktopServices
    {
        public IFileDialogService FileDialogs { get; } = new Dialogs();
        public IPreviewBitmapFactory Preview { get; } = new PreviewFactory();
        public Task OpenSettingsAsync(System.Windows.Window owner) => Task.CompletedTask;
        public void ShowMessage(System.Windows.Window owner, string message, string title) { }
    }

    private sealed class Dialogs : IFileDialogService
    {
        public IReadOnlyList<string> PickImages() => [];
        public string? PickSavePath(string suggestedName) => null;
        public string? PickFolder(string? currentFolder) => null;
    }

    private sealed class PreviewFactory : IPreviewBitmapFactory
    {
        public BitmapSource FromRgba(ProcessedImage image)
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bitmap.Freeze();
            return bitmap;
        }

        public BitmapSource? FromFile(string path, int decodePixelWidth = 0) => null;
    }

    private sealed class HistoryStore : IProcessedImageHistoryStore
    {
        private readonly Dictionary<Guid, ProcessedImage> _images = [];
        public List<ProcessedImageHistoryItem> Items { get; } = [];

        public Task<ProcessedImageHistoryItem> SaveAsync(ProcessedImage image, string originalFileName, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
        {
            var item = new ProcessedImageHistoryItem(Guid.NewGuid(), Path.GetFileName(originalFileName), processedAtUtc, Guid.NewGuid().ToString("N") + ".png");
            Items.Add(item);
            _images[item.Id] = image;
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<ProcessedImageHistoryItem>> EnumerateMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessedImageHistoryItem>>(Items.OrderByDescending(item => item.ProcessedAtUtc).ToArray());

        public Task<ProcessedImage?> LoadAsync(ProcessedImageHistoryItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(_images.GetValueOrDefault(item.Id));

        public string? GetImagePath(ProcessedImageHistoryItem item) => null;

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(item => item.Id == id);
            _images.Remove(id);
            return Task.CompletedTask;
        }

        public Task<int> CleanupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
