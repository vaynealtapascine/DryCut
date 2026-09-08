using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using BackgroundCut.Application.Ports;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Desktop.Ui;
using BackgroundCut.Domain.Models;
using BackgroundCut.Infrastructure;
using Xunit;

namespace BackgroundCut.Desktop.Tests;

public sealed class MainViewModelTests
{
    [AvaloniaFact]
    public void UnsupportedDropEntersErrorStateWithoutStartingEngine()
    {
        var engine = new FakeEngine();
        using var vm = CreateViewModel(engine);

        vm.DropPath("C:/images/photo.gif");

        Assert.True(vm.HasError);
        Assert.False(engine.Started);
        Assert.Contains("isn't supported", vm.Status);
    }

    [AvaloniaFact]
    public void DefaultsUseApproachableQualityCopy()
    {
        using var vm = CreateViewModel(new FakeEngine());

        Assert.Equal(ModelKind.FastAndAccurate, vm.Model);
        Assert.Equal(RefinementPreset.Balanced, vm.Refinement);
        Assert.Contains("recommended", vm.ModelDescription);
        Assert.Contains("balanced", vm.RefinementDescription);
        Assert.Contains("30 days", vm.RetentionNotice);
    }

    [AvaloniaFact]
    public async Task UnsupportedDropDoesNotCoverAnExistingResultPanel()
    {
        var history = new HistoryStore();
        await history.SaveAsync(new ProcessedImage([1, 2, 3, 255], 1, 1), "ready.png", DateTimeOffset.UtcNow);
        using var vm = CreateViewModel(new FakeEngine(), history);
        await vm.InitializeAsync();

        vm.DropPath("C:/images/not-supported.gif");

        Assert.True(vm.HasResult);
        Assert.False(vm.HasError);
        Assert.Contains("isn't supported", vm.Status);
    }

    [AvaloniaFact]
    public async Task EtaWarmsUpAgainForAProcessingProfileWithoutSamples()
    {
        var paths = CreateInputFiles(2);
        var engine = new FakeEngine(TimeSpan.FromMilliseconds(80));
        using var vm = CreateViewModel(engine);
        try
        {
            vm.DropPath(paths[0]);
            await vm.WhenQueueIsIdleAsync();
            vm.Model = ModelKind.HighestQuality;

            vm.DropPath(paths[1]);

            Assert.Contains("Estimating", vm.QueueEtaText, StringComparison.OrdinalIgnoreCase);
            await vm.WhenQueueIsIdleAsync();
        }
        finally
        {
            DeleteInputFiles(paths);
        }
    }

    [AvaloniaFact]
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
            await vm.InitializeAsync();

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

    [AvaloniaFact]
    public async Task PersistenceFailureKeepsResultCopyableAndWarnsAfterQueueFinishes()
    {
        var paths = CreateInputFiles(1);
        var history = new HistoryStore { SaveFailure = new IOException("history unavailable") };
        using var vm = CreateViewModel(new FakeEngine(), history);

        try
        {
            vm.DropPath(paths[0]);
            await vm.WhenQueueIsIdleAsync();

            var item = Assert.Single(vm.VisibleItems);
            Assert.True(item.IsCompleted);
            Assert.True(item.CanCopy);
            Assert.Null(item.HistoryItem);
            Assert.Contains("not stored", vm.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not saved to history", item.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteInputFiles(paths);
        }
    }

    [AvaloniaFact]
    public async Task RetryReplacesTheFailedEntryInsteadOfLeavingADuplicate()
    {
        var paths = CreateInputFiles(1);
        var engine = new FakeEngine(failuresBeforeSuccess: 1);
        using var vm = CreateViewModel(engine);

        try
        {
            vm.DropPath(paths[0]);
            await vm.WhenQueueIsIdleAsync();
            Assert.True(Assert.Single(vm.VisibleItems).HasFailed);

            vm.RetryCommand.Execute(null);
            await vm.WhenQueueIsIdleAsync();

            Assert.True(Assert.Single(vm.VisibleItems).IsCompleted);
            Assert.Equal(1, vm.TotalItemCount);
        }
        finally
        {
            DeleteInputFiles(paths);
        }
    }

    [AvaloniaFact]
    public async Task CancelDuringHistorySaveCancelsItemWithoutPersistingIt()
    {
        var paths = CreateInputFiles(1);
        var history = new HistoryStore { BlockSaves = true };
        using var vm = CreateViewModel(new FakeEngine(), history);

        try
        {
            vm.DropPath(paths[0]);
            await history.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelCommand.Execute(null);
            await vm.WhenQueueIsIdleAsync();

            var item = Assert.Single(vm.VisibleItems);
            Assert.True(item.HasFailed);
            Assert.Empty(history.Items);
        }
        finally
        {
            DeleteInputFiles(paths);
        }
    }

    [AvaloniaFact]
    public async Task DisposeCancelsAnInFlightQueueItem()
    {
        var paths = CreateInputFiles(1);
        var engine = new FakeEngine(blockUntilCancelled: true);
        var vm = CreateViewModel(engine);
        try
        {
            vm.DropPath(paths[0]);
            await engine.StartedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queueTask = vm.WhenQueueIsIdleAsync();

            vm.Dispose();
            await queueTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(vm.IsWorking);
            Assert.True(Assert.Single(vm.VisibleItems).HasFailed);
        }
        finally
        {
            vm.Dispose();
            DeleteInputFiles(paths);
        }
    }

    [AvaloniaFact]
    public void DisposeRaisesCanAdjustQualityChangedAndDisablesIt()
    {
        var vm = CreateViewModel(new FakeEngine());
        Assert.True(vm.CanAdjustQuality);
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => raisedProperties.Add(args.PropertyName);

        vm.Dispose();

        Assert.False(vm.CanAdjustQuality);
        Assert.Contains(nameof(MainViewModel.CanAdjustQuality), raisedProperties);
    }

    [AvaloniaFact]
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
        Assert.Equal("Showing 1–100 of 105 items", vm.GallerySummary);
        Assert.True(vm.HasOlderItems);
        Assert.Equal("image-0.png", vm.VisibleItems[0].DisplayName);

        vm.ShowOlderItemsCommand.Execute(null);

        Assert.Equal(5, vm.VisibleItems.Count);
        Assert.True(vm.HasNewerItems);
        Assert.False(vm.HasOlderItems);
        Assert.Equal("Showing 101–105 of 105 items", vm.GallerySummary);
        Assert.Equal("image-100.png", vm.VisibleItems[0].DisplayName);
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
    public async Task RetentionRefreshRemovesItemsThatExpireWhileAppRemainsOpen()
    {
        var history = new HistoryStore();
        await history.SaveAsync(
            new ProcessedImage([1, 2, 3, 255], 1, 1),
            "expiring.png",
            DateTimeOffset.UtcNow.AddDays(-29));
        using var vm = CreateViewModel(new FakeEngine(), history);
        await vm.InitializeAsync();
        Assert.Single(vm.VisibleItems);

        var existing = history.Items[0];
        history.Items[0] = new ProcessedImageHistoryItem(
            existing.Id,
            existing.OriginalFileName,
            DateTimeOffset.UtcNow.AddDays(-31),
            existing.PngPath);
        await vm.RefreshRetentionAsync();

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

    private sealed class FakeEngine(
        TimeSpan? delay = null,
        bool blockUntilCancelled = false,
        int failuresBeforeSuccess = 0) : IBackgroundRemovalEngine
    {
        private int _concurrency;
        public bool Started { get; private set; }
        public int MaximumConcurrency { get; private set; }
        public List<string> ProcessedPaths { get; } = [];
        public TaskCompletionSource StartedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                StartedSignal.TrySetResult();
                progress?.Report(new ProcessingProgress("Removing the background…", 0.5));
                if (blockUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (delay is not null) await Task.Delay(delay.Value, cancellationToken);
                if (failuresBeforeSuccess-- > 0) throw new InvalidOperationException("Simulated processing failure.");
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
        public Task OpenSettingsAsync() => Task.CompletedTask;
    }

    private sealed class Dialogs : IFileDialogService
    {
        public Task<IReadOnlyList<string>> PickImagesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string? currentFolder) => Task.FromResult<string?>(null);
    }

    private sealed class PreviewFactory : IPreviewBitmapFactory
    {
        public Bitmap FromRgba(ProcessedImage image)
        {
            using var stream = new MemoryStream();
            ImageSharpImageService.SavePngAsync(image, stream).GetAwaiter().GetResult();
            stream.Position = 0;
            return new Bitmap(stream);
        }

        public Bitmap? FromFile(string path, int decodePixelWidth = 0) => null;
    }

    private sealed class HistoryStore : IProcessedImageHistoryStore
    {
        private readonly Dictionary<Guid, ProcessedImage> _images = [];
        public List<ProcessedImageHistoryItem> Items { get; } = [];
        public Exception? SaveFailure { get; init; }
        public bool BlockSaves { get; init; }
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessedImageHistoryItem> SaveAsync(ProcessedImage image, string originalFileName, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult();
            if (BlockSaves)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (SaveFailure is not null)
                throw SaveFailure;

            var item = new ProcessedImageHistoryItem(Guid.NewGuid(), Path.GetFileName(originalFileName), processedAtUtc, Guid.NewGuid().ToString("N") + ".png");
            Items.Add(item);
            _images[item.Id] = image;
            return item;
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

        public Task<int> CleanupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cutoff = nowUtc.ToUniversalTime() - ProcessedImageHistoryPolicy.Retention;
            var expired = Items.Where(item => item.ProcessedAtUtc < cutoff).ToArray();
            foreach (var item in expired)
            {
                Items.Remove(item);
                _images.Remove(item.Id);
            }
            return Task.FromResult(expired.Length);
        }
    }
}
