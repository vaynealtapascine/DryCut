using System.Globalization;
using System.IO;
using Avalonia.Media.Imaging;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Desktop.Ui;

public enum QueueItemState
{
    Waiting,
    Processing,
    Completed,
    Failed,
    Cancelled
}

public sealed class QueueItemViewModel : ObservableObject
{
    private QueueItemState _state;
    private double _progress;
    private string _status;
    private string _errorDetails = "";
    private Bitmap? _thumbnail;
    private bool _isSelected;

    private QueueItemViewModel(
        Guid id,
        string displayName,
        DateTimeOffset addedAtUtc,
        QueueItemState state,
        string status,
        string? sourcePath,
        ProcessedImageHistoryItem? historyItem,
        ProcessingOptions options)
    {
        Id = id;
        DisplayName = displayName;
        AddedAtUtc = addedAtUtc;
        _state = state;
        _status = status;
        SourcePath = sourcePath;
        HistoryItem = historyItem;
        Options = options;
    }

    public Guid Id { get; }
    public string DisplayName { get; }
    public DateTimeOffset AddedAtUtc { get; }
    public string? SourcePath { get; }
    public ProcessedImageHistoryItem? HistoryItem { get; private set; }
    public ProcessingOptions Options { get; }
    internal ProcessedImage? InMemoryResult { get; private set; }

    public QueueItemState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsWaiting));
            Raise(nameof(IsProcessing));
            Raise(nameof(IsCompleted));
            Raise(nameof(HasFailed));
            Raise(nameof(CanCopy));
            Raise(nameof(CanDelete));
            Raise(nameof(StatusColor));
        }
    }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value)) Raise(nameof(SelectionAutomationName));
        }
    }
    public string ErrorDetails { get => _errorDetails; private set => Set(ref _errorDetails, value); }
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        internal set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            var previous = _thumbnail;
            if (Set(ref _thumbnail, value)) previous?.Dispose();
        }
    }
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (!Set(ref _isSelected, value)) return;
            Raise(nameof(SelectionBorderColor));
            Raise(nameof(SelectionBackgroundColor));
        }
    }

    public bool IsWaiting => State == QueueItemState.Waiting;
    public bool IsProcessing => State == QueueItemState.Processing;
    public bool IsCompleted => State == QueueItemState.Completed;
    public bool HasFailed => State is QueueItemState.Failed or QueueItemState.Cancelled;
    public bool CanCopy => IsCompleted && (HistoryItem is not null || InMemoryResult is not null);
    public bool CanDelete => !IsProcessing;
    public string TimestampText => AddedAtUtc.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string SelectionAutomationName => $"Select {DisplayName}, {Status}";
    public string SelectionBorderColor => IsSelected ? "#D97706" : "#E8D3AE";
    public string SelectionBackgroundColor => IsSelected ? "#FFF1CC" : "#FFFEF8";
    public string StatusColor => State switch
    {
        QueueItemState.Processing => "#D97706",
        QueueItemState.Completed => "#7A8B42",
        QueueItemState.Failed or QueueItemState.Cancelled => "#B84A3A",
        _ => "#A48A70"
    };

    public static QueueItemViewModel CreatePending(
        string sourcePath,
        DateTimeOffset addedAtUtc,
        Bitmap? thumbnail,
        ProcessingOptions options)
    {
        var item = new QueueItemViewModel(
            Guid.NewGuid(),
            Path.GetFileName(sourcePath),
            addedAtUtc.ToUniversalTime(),
            QueueItemState.Waiting,
            "Waiting",
            sourcePath,
            null,
            options)
        {
            Thumbnail = thumbnail
        };
        return item;
    }

    public static QueueItemViewModel FromHistory(ProcessedImageHistoryItem historyItem, Bitmap? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(historyItem);
        var item = new QueueItemViewModel(
            historyItem.Id,
            historyItem.OriginalFileName,
            historyItem.ProcessedAtUtc,
            QueueItemState.Completed,
            "Ready",
            null,
            historyItem,
            new ProcessingOptions())
        {
            Progress = 1,
            Thumbnail = thumbnail
        };
        return item;
    }

    internal void MarkProcessing()
    {
        State = QueueItemState.Processing;
        Progress = 0;
        Status = "Starting…";
        ErrorDetails = "";
    }

    internal void ReportProgress(string stage, double fraction)
    {
        Status = stage;
        Progress = Math.Clamp(fraction, 0, 1);
    }

    internal void MarkCompleted(ProcessedImage result, ProcessedImageHistoryItem? historyItem)
    {
        ArgumentNullException.ThrowIfNull(result);
        HistoryItem = historyItem;
        InMemoryResult = historyItem is null ? result : null;
        Progress = 1;
        Status = historyItem is null ? "Ready · not saved to history" : "Ready";
        State = QueueItemState.Completed;
        Raise(nameof(CanCopy));
    }

    internal void MarkFailed(string message, Exception exception)
    {
        Status = message;
        ErrorDetails = exception.ToString();
        State = QueueItemState.Failed;
    }

    internal void MarkCancelled()
    {
        Status = "Cancelled";
        ErrorDetails = "";
        State = QueueItemState.Cancelled;
    }
}
