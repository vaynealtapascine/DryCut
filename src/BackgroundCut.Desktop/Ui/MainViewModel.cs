using System.IO;
using System.Windows.Media.Imaging;
using BackgroundCut.Application.Ports;
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
    private CancellationTokenSource? _cancellation;
    private ProcessedImage? _result;
    private string? _sourcePath;
    private WorkflowState _state = WorkflowState.Empty;
    private string _status = "Drop an image here, or choose one to begin.";
    private string _errorDetails = "";
    private double _progress;
    private ModelKind _model = ModelKind.FastAndAccurate;
    private RefinementPreset _refinement = RefinementPreset.Balanced;
    private BitmapSource? _before;
    private BitmapSource? _after;

    public MainViewModel(
        RemoveBackgroundUseCase remove,
        ExportImageUseCase export,
        IClipboardService clipboard,
        ISettingsStore settings,
        IDesktopServices desktop)
    {
        _remove = remove;
        _export = export;
        _clipboard = clipboard;
        _settings = settings;
        _desktop = desktop;

        ChooseImageCommand = new RelayCommand(_ => ChooseImage(), _ => !IsWorking);
        CopyCommand = new AsyncCommand(_ => CopyAsync(), _ => HasResult);
        SaveCommand = new AsyncCommand(_ => SaveAsync(), _ => HasResult);
        SaveAsCommand = new AsyncCommand(_ => SaveAsAsync(), _ => HasResult);
        ApplyQualityCommand = new RelayCommand(_ => StartProcessing(_sourcePath), _ => !IsWorking && _sourcePath is not null);
        NewImageCommand = new RelayCommand(_ => Reset(), _ => !IsWorking && (_sourcePath is not null || HasError));
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsWorking);
        SettingsCommand = new AsyncCommand(_ => OpenSettingsAsync(), _ => !IsWorking);
        RetryCommand = new RelayCommand(_ => StartProcessing(_sourcePath), _ => HasError && _sourcePath is not null);
    }

    public WorkflowState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(IsEmpty));
                Raise(nameof(IsWorking));
                Raise(nameof(HasResult));
                Raise(nameof(HasError));
                Raise(nameof(CanAdjustQuality));
                RefreshCommands();
            }
        }
    }

    public bool IsEmpty => State == WorkflowState.Empty;
    public bool IsWorking => State == WorkflowState.Working;
    public bool HasResult => State == WorkflowState.Result;
    public bool HasError => State == WorkflowState.Error;
    public bool CanAdjustQuality => !IsWorking;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ErrorDetails { get => _errorDetails; private set => Set(ref _errorDetails, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string FileName => _sourcePath is null ? "" : Path.GetFileName(_sourcePath);
    public BitmapSource? Before { get => _before; private set => Set(ref _before, value); }
    public BitmapSource? After { get => _after; private set => Set(ref _after, value); }

    public ModelKind Model
    {
        get => _model;
        set
        {
            if (Set(ref _model, value))
            {
                Raise(nameof(ModelDescription));
                ApplyQualityCommand.Refresh();
            }
        }
    }

    public RefinementPreset Refinement
    {
        get => _refinement;
        set
        {
            if (Set(ref _refinement, value))
            {
                Raise(nameof(RefinementDescription));
                ApplyQualityCommand.Refresh();
            }
        }
    }

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

    public static IReadOnlyList<Choice<ModelKind>> ModelChoices { get; } = new[]
    {
        new Choice<ModelKind>(ModelKind.FastAndAccurate, "Fast & accurate"),
        new Choice<ModelKind>(ModelKind.HighestQuality, "Highest quality")
    };

    public static IReadOnlyList<Choice<RefinementPreset>> RefinementChoices { get; } = new[]
    {
        new Choice<RefinementPreset>(RefinementPreset.None, "None"),
        new Choice<RefinementPreset>(RefinementPreset.Soft, "Soft"),
        new Choice<RefinementPreset>(RefinementPreset.Balanced, "Balanced"),
        new Choice<RefinementPreset>(RefinementPreset.Detailed, "Detailed")
    };

    public RelayCommand ChooseImageCommand { get; }
    public AsyncCommand CopyCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand SaveAsCommand { get; }
    public RelayCommand ApplyQualityCommand { get; }
    public RelayCommand NewImageCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand SettingsCommand { get; }
    public RelayCommand RetryCommand { get; }

    public void ChooseImage() => StartProcessing(_desktop.FileDialogs.PickImage());
    public void DropPath(string? path) => StartProcessing(path);

    private void StartProcessing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path) || !ImageInput.SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            SetError("That file type isn't supported. Choose a JPG, PNG, WebP, BMP, or TIFF image.", null);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            SetError("That image could not be found. It may have been moved or deleted.", null);
            return;
        }

        _sourcePath = fullPath;
        Raise(nameof(FileName));
        Before = _desktop.Preview.FromFile(_sourcePath);
        _ = ProcessAsync(_sourcePath);
    }

    private async Task ProcessAsync(string path)
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        State = WorkflowState.Working;
        Status = Model == ModelKind.HighestQuality ? "Using highest quality…" : "Removing the background…";
        Progress = 0;
        ErrorDetails = "";
        try
        {
            var progress = new Progress<ProcessingProgress>(value =>
            {
                Status = value.Stage;
                Progress = Math.Clamp(value.Fraction, 0, 1);
            });
            _result = await _remove.ExecuteAsync(
                new ImageInput(path),
                new ProcessingOptions(Model, new RefinementSettings(Refinement)),
                progress,
                _cancellation.Token);
            After = _desktop.Preview.FromRgba(_result);
            Progress = 1;
            Status = "Background removed. Your original image is unchanged.";
            State = WorkflowState.Result;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. Your original image is unchanged.";
            State = _result is null ? WorkflowState.Empty : WorkflowState.Result;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("model is not installed", StringComparison.OrdinalIgnoreCase))
        {
            SetError("Highest quality needs a one-time download. Open Settings, download it, then try again.", ex);
        }
        catch (Exception ex)
        {
            SetError("We couldn't remove the background. Try another image or try again.", ex);
        }
    }

    private void SetError(string message, Exception? exception)
    {
        State = WorkflowState.Error;
        Status = message;
        ErrorDetails = exception?.ToString() ?? "";
    }

    private async Task CopyAsync()
    {
        if (_result is null) return;
        try
        {
            await _clipboard.CopyAsync(_result, CancellationToken.None);
            Status = "Copied the transparent image to your clipboard.";
        }
        catch (Exception ex)
        {
            SetError("Copying didn't work. You can still use Save as…", ex);
        }
    }

    private async Task SaveAsync()
    {
        if (_result is null) return;
        var settings = await _settings.LoadExportSettingsAsync(CancellationToken.None);
        string? requestedPath = null;
        if (settings.Policy == ExportPolicy.AskEveryTime)
        {
            requestedPath = _desktop.FileDialogs.PickSavePath(GetSuggestedName());
            if (requestedPath is null) return;
        }
        await ExportAsync(requestedPath);
    }

    private async Task SaveAsAsync()
    {
        if (_result is null) return;
        var path = _desktop.FileDialogs.PickSavePath(GetSuggestedName());
        if (path is not null) await ExportAsync(path);
    }

    private async Task ExportAsync(string? requestedPath)
    {
        if (_result is null) return;
        try
        {
            var file = await _export.ExecuteAsync(
                _result,
                Path.GetDirectoryName(_sourcePath),
                requestedPath,
                GetSuggestedName());
            Status = $"Saved a transparent PNG: {Path.GetFileName(file.Path)}";
        }
        catch (Exception ex)
        {
            SetError("We couldn't save the image. Check the folder and try again.", ex);
        }
    }

    private string GetSuggestedName() => Path.GetFileNameWithoutExtension(FileName) + "-background-removed.png";

    private async Task OpenSettingsAsync()
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null) await _desktop.OpenSettingsAsync(owner);
    }

    private void Reset()
    {
        _cancellation?.Cancel();
        _result = null;
        _sourcePath = null;
        Before = After = null;
        Progress = 0;
        ErrorDetails = "";
        Status = "Drop an image here, or choose one to begin.";
        State = WorkflowState.Empty;
        Raise(nameof(FileName));
    }

    private void RefreshCommands()
    {
        ChooseImageCommand.Refresh();
        CopyCommand.Refresh();
        SaveCommand.Refresh();
        SaveAsCommand.Refresh();
        ApplyQualityCommand.Refresh();
        CancelCommand.Refresh();
        NewImageCommand.Refresh();
        SettingsCommand.Refresh();
        RetryCommand.Refresh();
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }
}
