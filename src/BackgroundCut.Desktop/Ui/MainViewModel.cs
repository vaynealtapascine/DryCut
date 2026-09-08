using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BackgroundCut.Application.Ports;
using BackgroundCut.Application.Queue;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Desktop.Ui;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly RemoveBackgroundUseCase _remove;
    private readonly ExportImageUseCase _export;
    private readonly IClipboardService _clipboard;
    private readonly ISettingsStore _settings;
    private readonly IDesktopServices _desktop;
    private readonly IProcessedImageHistoryStore? _history;
    private readonly QueueEtaEstimator _eta = new(minimumSamples: 1);
    private readonly Queue<QueueItemViewModel> _pending = new();
    private readonly List<QueueItemViewModel> _allItems = [];
    private readonly DispatcherTimer _etaTimer;
    private readonly Stopwatch _activeStopwatch = new();

    private CancellationTokenSource? _activeCancellation;
    private Task? _queueTask;
    private QueueItemViewModel? _activeItem;
    private QueueItemViewModel? _selectedItem;
    private ProcessedImage? _selectedResult;
    private string _status = "Drop images here, or choose images to begin.";
    private string _errorDetails = "";
    private ModelKind _model = ModelKind.FastAndAccurate;
    private RefinementPreset _refinement = RefinementPreset.Balanced;
    private BitmapSource? _before;
    private BitmapSource? _after;
    private bool _standaloneError;
    private bool _initialized;
    private bool _disposed;
    private int _selectionVersion;

    public MainViewModel(
        RemoveBackgroundUseCase remove,
        ExportImageUseCase export,
        IClipboardService clipboard,
        ISettingsStore settings,
        IDesktopServices desktop,
        IProcessedImageHistoryStore? history = null)
    {
        _remove = remove ?? throw new ArgumentNullException(nameof(remove));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _history = history;

        ChooseImageCommand = new RelayCommand(_ => ChooseImages());
        CopyCommand = new AsyncCommand(_ => CopyItemAsync(SelectedItem), _ => SelectedItem?.CanCopy == true);
        SaveCommand = new AsyncCommand(_ => SaveAsync(), _ => HasResult);
        SaveAsCommand = new AsyncCommand(_ => SaveAsAsync(), _ => HasResult);
        ApplyQualityCommand = new RelayCommand(_ => ReprocessSelected(), _ => CanReprocessSelected);
        NewImageCommand = new RelayCommand(_ => ChooseImages());
        CancelCommand = new RelayCommand(_ => _activeCancellation?.Cancel(), _ => IsWorking);
        SettingsCommand = new AsyncCommand(_ => OpenSettingsAsync(), _ => !IsWorking);
        RetryCommand = new RelayCommand(_ => RetrySelected(), _ => SelectedItem?.HasFailed == true && SelectedItem.SourcePath is not null);
        SelectQueueItemCommand = new AsyncCommand(parameter => SelectItemAsync(parameter as QueueItemViewModel), parameter => parameter is QueueItemViewModel);
        CopyQueueItemCommand = new AsyncCommand(parameter => CopyItemAsync(parameter as QueueItemViewModel), parameter => parameter is QueueItemViewModel item && item.CanCopy);
        DeleteQueueItemCommand = new AsyncCommand(parameter => DeleteItemAsync(parameter as QueueItemViewModel), parameter => parameter is QueueItemViewModel item && item.CanDelete);

        _etaTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _etaTimer.Tick += OnEtaTick;
    }

    public ObservableCollection<QueueItemViewModel> VisibleItems { get; } = [];

    public QueueItemViewModel? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            if (_selectedItem is not null) _selectedItem.IsSelected = false;
            if (!Set(ref _selectedItem, value)) return;
            if (_selectedItem is not null) _selectedItem.IsSelected = true;
            Raise(nameof(FileName));
            RefreshViewState();
        }
    }

    public bool IsEmpty => SelectedItem is null;
    public bool IsWorking => _activeItem is not null;
    public bool ShowProcessing => SelectedItem?.State is QueueItemState.Waiting or QueueItemState.Processing;
    public bool HasResult => SelectedItem?.IsCompleted == true;
    public bool HasError => _standaloneError || SelectedItem?.HasFailed == true;
    public bool HasBefore => Before is not null;
    public bool CanAdjustQuality => !_disposed;
    public bool CanReprocessSelected => SelectedItem?.SourcePath is string path && File.Exists(path);
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ErrorDetails { get => _errorDetails; private set => Set(ref _errorDetails, value); }
    public double Progress => _activeItem?.Progress ?? 0;
    public string FileName => SelectedItem?.DisplayName ?? "";
    public BitmapSource? Before { get => _before; private set { if (Set(ref _before, value)) Raise(nameof(HasBefore)); } }
    public BitmapSource? After { get => _after; private set => Set(ref _after, value); }
    public int QueueCount => _pending.Count + (IsWorking ? 1 : 0);
    public int TotalItemCount => _allItems.Count;
    public int HiddenItemCount => Math.Max(0, TotalItemCount - VisibleItems.Count);
    public bool HasHiddenItems => HiddenItemCount > 0;
    public string QueueEtaText => QueueCount == 0 ? "All caught up" : _eta.Current.DisplayText;
    public string QueueSummary => QueueCount switch
    {
        0 => "Queue is clear",
        1 => $"1 image left · {QueueEtaText}",
        _ => $"{QueueCount} images left · {QueueEtaText}"
    };
    public string GallerySummary => HiddenItemCount > 0
        ? $"Showing 100 of {TotalItemCount} items · {HiddenItemCount} stored off-screen"
        : $"{TotalItemCount} {(TotalItemCount == 1 ? "item" : "items")}";
    public string RetentionNotice { get; } = "Processed images stay here for 30 days, then are deleted unless you save them manually.";

    public string ModelDescription => Model == ModelKind.FastAndAccurate
        ? "The recommended everyday model. Included and usually finishes quickly."
        : "A larger, stronger model for hair, fur, and busy backgrounds. Download it once in Settings.";

    public string RefinementDescription => Refinement switch
    {
        RefinementPreset.None => "Keep the model's original edge.",
        RefinementPreset.Soft => "Gently feather hard edges.",
        RefinementPreset.Detailed => "Spend more time preserving fine edge detail.",
        _ => "A balanced cleanup that avoids making edges look overly soft."
    };

    public ModelKind Model
    {
        get => _model;
        set
        {
            if (!Set(ref _model, value)) return;
            Raise(nameof(ModelDescription));
            ApplyQualityCommand.Refresh();
        }
    }

    public RefinementPreset Refinement
    {
        get => _refinement;
        set
        {
            if (!Set(ref _refinement, value)) return;
            Raise(nameof(RefinementDescription));
            ApplyQualityCommand.Refresh();
        }
    }

    public static IReadOnlyList<Choice<ModelKind>> ModelChoices { get; } =
    [
        new(ModelKind.FastAndAccurate, "Fast & accurate"),
        new(ModelKind.HighestQuality, "Highest quality")
    ];

    public static IReadOnlyList<Choice<RefinementPreset>> RefinementChoices { get; } =
    [
        new(RefinementPreset.None, "None"),
        new(RefinementPreset.Soft, "Soft"),
        new(RefinementPreset.Balanced, "Balanced"),
        new(RefinementPreset.Detailed, "Detailed")
    ];

    public RelayCommand ChooseImageCommand { get; }
    public AsyncCommand CopyCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand SaveAsCommand { get; }
    public RelayCommand ApplyQualityCommand { get; }
    public RelayCommand NewImageCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand SettingsCommand { get; }
    public RelayCommand RetryCommand { get; }
    public AsyncCommand SelectQueueItemCommand { get; }
    public AsyncCommand CopyQueueItemCommand { get; }
    public AsyncCommand DeleteQueueItemCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized || _history is null) return;
        _initialized = true;

        try
        {
            await _history.CleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            var history = await _history.EnumerateMetadataAsync(CancellationToken.None);
            foreach (var metadata in history)
            {
                if (_allItems.Any(item => item.Id == metadata.Id)) continue;
                _allItems.Add(QueueItemViewModel.FromHistory(metadata));
            }
            RefreshVisibleItems();

            if (SelectedItem is null && VisibleItems.Count > 0)
                await SelectItemAsync(VisibleItems[0]);
        }
        catch
        {
            // History is supplementary. An unavailable folder must not prevent processing new work.
        }
    }

    public void ChooseImages()
    {
        try
        {
            EnqueuePaths(_desktop.FileDialogs.PickImages());
        }
        catch (Exception exception)
        {
            _standaloneError = true;
            Status = "The image picker could not be opened. Try dragging images into the window instead.";
            ErrorDetails = exception.ToString();
            RefreshViewState();
        }
    }
    public void DropPath(string? path) => EnqueuePaths(path is null ? [] : [path]);
    public void DropPaths(IEnumerable<string> paths) => EnqueuePaths(paths);

    public Task WhenQueueIsIdleAsync() => _queueTask ?? Task.CompletedTask;

    private void EnqueuePaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var added = 0;
        var rejected = 0;
        string? lastRejection = null;
        var options = new ProcessingOptions(Model, new RefinementSettings(Refinement));

        foreach (var candidate in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!Path.IsPathFullyQualified(candidate) || !ImageInput.SupportedExtensions.Contains(Path.GetExtension(candidate)))
            {
                rejected++;
                lastRejection = "That file type isn't supported. Choose JPG, PNG, WebP, BMP, or TIFF images.";
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                rejected++;
                lastRejection = "Some images were skipped because their file names were not valid.";
                continue;
            }
            if (!File.Exists(fullPath))
            {
                rejected++;
                lastRejection = "Some images were skipped because they could not be found.";
                continue;
            }

            var item = QueueItemViewModel.CreatePending(fullPath, DateTimeOffset.UtcNow, null, options);
            var insertionIndex = _allItems.FindIndex(existing => existing.State is QueueItemState.Completed or QueueItemState.Failed or QueueItemState.Cancelled);
            _allItems.Insert(insertionIndex < 0 ? _allItems.Count : insertionIndex, item);
            _pending.Enqueue(item);
            added++;
        }

        if (added > 0)
        {
            _standaloneError = false;
            ErrorDetails = "";
            _eta.Enqueue(added);
            Status = rejected == 0
                ? $"Added {added} {(added == 1 ? "image" : "images")} to the queue."
                : $"Added {added} images. {rejected} skipped.";
            RefreshVisibleItems();
            if (SelectedItem is null)
                _ = SelectItemAsync(_pending.Peek());
            StartQueueIfNeeded();
        }
        else if (rejected > 0)
        {
            _standaloneError = true;
            Status = lastRejection ?? "No supported images were added.";
            ErrorDetails = "";
            RefreshViewState();
        }
    }

    private void StartQueueIfNeeded()
    {
        if (_queueTask is not null) return;
        var task = ProcessQueueAsync();
        _queueTask = task.IsCompleted ? null : task;
    }

    private async Task ProcessQueueAsync()
    {
        var hadFailures = false;
        try
        {
            while (!_disposed && _pending.Count > 0)
            {
                var item = _pending.Dequeue();
                _activeItem = item;
                _activeCancellation = new CancellationTokenSource();
                _eta.StartNext();
                _activeStopwatch.Restart();
                _etaTimer.Start();
                item.MarkProcessing();
                if (SelectedItem is null || SelectedItem.State is QueueItemState.Waiting or QueueItemState.Processing)
                    await SelectItemAsync(item);
                RefreshQueueState();

                try
                {
                    var progress = new Progress<ProcessingProgress>(value =>
                    {
                        item.ReportProgress(value.Stage, value.Fraction);
                        if (ReferenceEquals(SelectedItem, item))
                            Status = value.Stage;
                        Raise(nameof(Progress));
                    });
                    var result = await _remove.ExecuteAsync(
                        new ImageInput(item.SourcePath!),
                        item.Options,
                        progress,
                        _activeCancellation.Token);

                    ProcessedImageHistoryItem? historyItem = null;
                    Exception? historyFailure = null;
                    if (_history is not null)
                    {
                        try
                        {
                            historyItem = await _history.SaveAsync(result, item.DisplayName, DateTimeOffset.UtcNow, CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            historyFailure = exception;
                        }
                    }

                    _activeStopwatch.Stop();
                    _eta.CompleteActive(_activeStopwatch.Elapsed);
                    item.MarkCompleted(result, historyItem);
                    if (historyItem is not null)
                    {
                        var duplicate = _allItems.FirstOrDefault(existing =>
                            !ReferenceEquals(existing, item) && existing.HistoryItem?.Id == historyItem.Id);
                        if (duplicate is not null)
                            _allItems.Remove(duplicate);
                        var imagePath = _history?.GetImagePath(historyItem);
                        if (imagePath is not null)
                            item.Thumbnail = _desktop.Preview.FromFile(imagePath, 180);
                    }

                    if (ReferenceEquals(SelectedItem, item))
                    {
                        _selectedResult = result;
                        After = _desktop.Preview.FromRgba(result);
                        ErrorDetails = historyFailure?.ToString() ?? "";
                        Status = historyFailure is null
                            ? "Background removed. Your original image is unchanged."
                            : "Background removed, but its 30-day gallery copy could not be stored. Save it manually to keep it.";
                    }
                }
                catch (OperationCanceledException)
                {
                    hadFailures = true;
                    _activeStopwatch.Stop();
                    _eta.CancelActive();
                    item.MarkCancelled();
                    if (ReferenceEquals(SelectedItem, item))
                        Status = "Cancelled. Your original image is unchanged.";
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("model is not installed", StringComparison.OrdinalIgnoreCase))
                {
                    hadFailures = true;
                    _activeStopwatch.Stop();
                    _eta.FailActive();
                    item.MarkFailed("Highest quality needs a one-time download. Open Settings, download it, then try again.", exception);
                    SetSelectedError(item);
                }
                catch (Exception exception)
                {
                    hadFailures = true;
                    _activeStopwatch.Stop();
                    _eta.FailActive();
                    item.MarkFailed("We couldn't remove the background. Try this image again or choose another one.", exception);
                    SetSelectedError(item);
                }
                finally
                {
                    _etaTimer.Stop();
                    _activeCancellation.Dispose();
                    _activeCancellation = null;
                    _activeItem = null;
                    RefreshVisibleItems();
                    RefreshQueueState();
                }
            }
        }
        finally
        {
            _queueTask = null;
            if (!_disposed)
                Status = hadFailures ? "Queue finished. Some images need attention." : "All queued images are ready.";
            RefreshQueueState();
        }
    }

    private async Task SelectItemAsync(QueueItemViewModel? item)
    {
        if (item is null || !_allItems.Contains(item)) return;
        var version = ++_selectionVersion;
        SelectedItem = item;
        _standaloneError = false;
        _selectedResult = null;
        Before = item.SourcePath is not null && File.Exists(item.SourcePath)
            ? _desktop.Preview.FromFile(item.SourcePath, 1400)
            : null;
        After = null;
        ErrorDetails = item.ErrorDetails;
        Status = item.Status;

        if (item.IsCompleted)
        {
            var result = item.InMemoryResult;
            if (result is null && item.HistoryItem is not null && _history is not null)
                result = await _history.LoadAsync(item.HistoryItem, CancellationToken.None);
            if (version != _selectionVersion || !ReferenceEquals(SelectedItem, item)) return;

            if (result is null)
            {
                Status = "This gallery image is no longer available. You can delete this entry.";
                return;
            }

            _selectedResult = result;
            After = _desktop.Preview.FromRgba(result);
            Status = "Ready to copy or save.";
        }
        RefreshViewState();
    }

    private async Task<ProcessedImage?> GetResultAsync(QueueItemViewModel? item)
    {
        if (item?.IsCompleted != true) return null;
        if (ReferenceEquals(item, SelectedItem) && _selectedResult is not null) return _selectedResult;
        if (item.InMemoryResult is not null) return item.InMemoryResult;
        if (item.HistoryItem is null || _history is null) return null;
        return await _history.LoadAsync(item.HistoryItem, CancellationToken.None);
    }

    private async Task CopyItemAsync(QueueItemViewModel? item)
    {
        var result = await GetResultAsync(item);
        if (result is null)
        {
            Status = "That image is no longer available to copy.";
            return;
        }

        try
        {
            await _clipboard.CopyAsync(result, CancellationToken.None);
            Status = $"Copied {item!.DisplayName} to your clipboard.";
        }
        catch (Exception exception)
        {
            Status = "Copying didn't work. You can still select the image and use Save as…";
            ErrorDetails = exception.ToString();
        }
    }

    private async Task DeleteItemAsync(QueueItemViewModel? item)
    {
        if (item is null || item.IsProcessing || !_allItems.Contains(item)) return;

        if (item.IsWaiting)
        {
            var retained = _pending.Where(queued => !ReferenceEquals(queued, item)).ToArray();
            _pending.Clear();
            foreach (var queued in retained) _pending.Enqueue(queued);
            _eta.RemovePending();
        }
        else if (item.HistoryItem is not null && _history is not null)
        {
            try
            {
                await _history.DeleteAsync(item.HistoryItem.Id, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Status = "That gallery image could not be deleted. Close other apps using it and try again.";
                ErrorDetails = exception.ToString();
                return;
            }
        }

        var wasSelected = ReferenceEquals(SelectedItem, item);
        _allItems.Remove(item);
        RefreshVisibleItems();
        if (wasSelected)
        {
            SelectedItem = null;
            Before = null;
            After = null;
            _selectedResult = null;
            if (VisibleItems.Count > 0)
                await SelectItemAsync(VisibleItems[0]);
            else
                Status = "Drop images here, or choose images to begin.";
        }
        RefreshQueueState();
    }

    private void ReprocessSelected()
    {
        if (SelectedItem?.SourcePath is string path)
            EnqueuePaths([path]);
    }

    private void RetrySelected()
    {
        if (SelectedItem?.SourcePath is string path)
            EnqueuePaths([path]);
    }

    private async Task SaveAsync()
    {
        if (SelectedItem is null) return;
        try
        {
            var settings = await _settings.LoadExportSettingsAsync(CancellationToken.None);
            string? requestedPath = null;
            if (settings.Policy == ExportPolicy.AskEveryTime)
            {
                requestedPath = _desktop.FileDialogs.PickSavePath(GetSuggestedName());
                if (requestedPath is null) return;
            }
            await ExportAsync(requestedPath);
        }
        catch (Exception exception)
        {
            Status = "We couldn't prepare the save location. Open Settings and check the output folder.";
            ErrorDetails = exception.ToString();
        }
    }

    private async Task SaveAsAsync()
    {
        if (SelectedItem is null) return;
        try
        {
            var path = _desktop.FileDialogs.PickSavePath(GetSuggestedName());
            if (path is not null) await ExportAsync(path);
        }
        catch (Exception exception)
        {
            Status = "We couldn't open the save dialog. Try Save instead.";
            ErrorDetails = exception.ToString();
        }
    }

    private async Task ExportAsync(string? requestedPath)
    {
        var result = await GetResultAsync(SelectedItem);
        if (result is null)
        {
            Status = "That image is no longer available to save.";
            return;
        }

        try
        {
            var sourceFolder = SelectedItem?.SourcePath is null ? null : Path.GetDirectoryName(SelectedItem.SourcePath);
            var file = await _export.ExecuteAsync(result, sourceFolder, requestedPath, GetSuggestedName());
            Status = $"Saved a permanent copy: {Path.GetFileName(file.Path)}";
        }
        catch (Exception exception)
        {
            Status = "We couldn't save the image. Check the folder and try again.";
            ErrorDetails = exception.ToString();
        }
    }

    private string GetSuggestedName() => Path.GetFileNameWithoutExtension(FileName) + "-background-removed.png";

    private async Task OpenSettingsAsync()
    {
        try
        {
            var owner = System.Windows.Application.Current?.MainWindow;
            if (owner is not null) await _desktop.OpenSettingsAsync(owner);
        }
        catch (Exception exception)
        {
            Status = "Settings could not be opened.";
            ErrorDetails = exception.ToString();
        }
    }

    private void SetSelectedError(QueueItemViewModel item)
    {
        if (!ReferenceEquals(SelectedItem, item)) return;
        Status = item.Status;
        ErrorDetails = item.ErrorDetails;
        RefreshViewState();
    }

    private void RefreshVisibleItems()
    {
        var queued = _allItems
            .Where(item => item.State is QueueItemState.Waiting or QueueItemState.Processing)
            .OrderBy(item => item.AddedAtUtc);
        var gallery = _allItems
            .Where(item => item.State is not (QueueItemState.Waiting or QueueItemState.Processing))
            .OrderByDescending(item => item.AddedAtUtc);
        var visible = queued
            .Concat(gallery)
            .Take(ProcessedImageHistoryPolicy.GalleryPreviewLimit)
            .ToArray();
        VisibleItems.Clear();
        foreach (var item in visible)
        {
            if (item.Thumbnail is null)
            {
                var previewPath = item.SourcePath;
                if (previewPath is null && item.HistoryItem is not null)
                    previewPath = _history?.GetImagePath(item.HistoryItem);
                if (previewPath is not null)
                    item.Thumbnail = _desktop.Preview.FromFile(previewPath, 180);
            }
            VisibleItems.Add(item);
        }
        Raise(nameof(TotalItemCount));
        Raise(nameof(HiddenItemCount));
        Raise(nameof(HasHiddenItems));
        Raise(nameof(GallerySummary));
    }

    private void RefreshQueueState()
    {
        Raise(nameof(IsWorking));
        Raise(nameof(QueueCount));
        Raise(nameof(QueueEtaText));
        Raise(nameof(QueueSummary));
        Raise(nameof(Progress));
        RefreshViewState();
    }

    private void RefreshViewState()
    {
        Raise(nameof(IsEmpty));
        Raise(nameof(ShowProcessing));
        Raise(nameof(HasResult));
        Raise(nameof(HasError));
        Raise(nameof(CanReprocessSelected));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        CopyCommand.Refresh();
        SaveCommand.Refresh();
        SaveAsCommand.Refresh();
        ApplyQualityCommand.Refresh();
        CancelCommand.Refresh();
        SettingsCommand.Refresh();
        RetryCommand.Refresh();
        SelectQueueItemCommand.Refresh();
        CopyQueueItemCommand.Refresh();
        DeleteQueueItemCommand.Refresh();
    }

    private void OnEtaTick(object? sender, EventArgs e)
    {
        if (!_eta.HasActiveItem) return;
        _eta.UpdateActiveElapsed(_activeStopwatch.Elapsed);
        Raise(nameof(QueueEtaText));
        Raise(nameof(QueueSummary));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _etaTimer.Stop();
        _etaTimer.Tick -= OnEtaTick;
        _activeCancellation?.Cancel();
        _activeCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }
}
