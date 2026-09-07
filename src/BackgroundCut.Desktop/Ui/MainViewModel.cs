using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BackgroundCut.Application.Ports;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Desktop.Ui;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly RemoveBackgroundUseCase _remove;
    private readonly ExportImageUseCase _export;
    private readonly IClipboardService _clipboard;
    private readonly IDesktopServices _desktop;
    private readonly IModelCatalog _models;
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

    public MainViewModel(RemoveBackgroundUseCase remove, ExportImageUseCase export, IClipboardService clipboard, IModelCatalog models, IDesktopServices desktop)
    { _remove = remove; _export = export; _clipboard = clipboard; _models = models; _desktop = desktop; }

    public WorkflowState State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(IsEmpty)); Raise(nameof(IsWorking)); Raise(nameof(HasResult)); Raise(nameof(HasError)); RefreshCommands(); } } }
    public bool IsEmpty => State == WorkflowState.Empty;
    public bool IsWorking => State == WorkflowState.Working;
    public bool HasResult => State == WorkflowState.Result;
    public bool HasError => State == WorkflowState.Error;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ErrorDetails { get => _errorDetails; private set => Set(ref _errorDetails, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string FileName => _sourcePath is null ? "" : Path.GetFileName(_sourcePath);
    public BitmapSource? Before { get => _before; private set => Set(ref _before, value); }
    public BitmapSource? After { get => _after; private set => Set(ref _after, value); }
    public ModelKind Model { get => _model; set { if (Set(ref _model, value)) Raise(nameof(ModelDescription)); } }
    public RefinementPreset Refinement { get => _refinement; set { if (Set(ref _refinement, value)) Raise(nameof(RefinementDescription)); } }
    public string ModelDescription => Model == ModelKind.FastAndAccurate ? "Fast & accurate — the recommended everyday choice." : "Highest quality — finer edges, with a little more time.";
    public string RefinementDescription => Refinement switch { RefinementPreset.None => "Original model edges.", RefinementPreset.Soft => "A gentle feather for a softer edge.", RefinementPreset.Detailed => "Most detail around hair and fine objects.", _ => "A balanced edge that works well for most images." };
    public static Array Models => Enum.GetValues<ModelKind>();
    public static Array Refinements => Enum.GetValues<RefinementPreset>();

    public RelayCommand ChooseImageCommand => new(_ => ChooseImage());
    public AsyncCommand CopyCommand => new(_ => CopyAsync(), _ => HasResult);
    public AsyncCommand SaveCommand => new(_ => SaveAsync(), _ => HasResult);
    public AsyncCommand SaveAsCommand => new(_ => SaveAsAsync(), _ => HasResult);
    public RelayCommand NewImageCommand => new(_ => Reset(), _ => !IsWorking);
    public RelayCommand CancelCommand => new(_ => _cancellation?.Cancel(), _ => IsWorking);
    public AsyncCommand SettingsCommand => new(async _ => await _desktop.OpenSettingsAsync(System.Windows.Application.Current.MainWindow));
    public RelayCommand RetryCommand => new(_ => StartProcessing(_sourcePath), _ => HasError && _sourcePath is not null);

    public void ChooseImage() => StartProcessing(_desktop.FileDialogs.PickImage());
    public void DropPath(string? path) => StartProcessing(path);
    private void StartProcessing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path) || !ImageInput.SupportedExtensions.Contains(Path.GetExtension(path))) { SetError("That file type isn't supported. Choose a JPG, PNG, WebP, BMP, or TIFF image.", null); return; }
        _sourcePath = Path.GetFullPath(path); Raise(nameof(FileName)); Before = _desktop.Preview.FromFile(_sourcePath); _ = ProcessAsync(_sourcePath);
    }
    private async Task ProcessAsync(string path)
    {
        _cancellation?.Cancel(); _cancellation = new(); State = WorkflowState.Working; Status = "Removing the background…"; Progress = 0; ErrorDetails = "";
        try { var progress = new Progress<ProcessingProgress>(p => { Status = p.Stage; Progress = Math.Clamp(p.Fraction, 0, 1); }); _result = await _remove.ExecuteAsync(new ImageInput(path), new ProcessingOptions(Model, new RefinementSettings(Refinement)), progress, _cancellation.Token); After = _desktop.Preview.FromRgba(_result); Progress = 1; Status = "Background removed. Your original image is unchanged."; State = WorkflowState.Result; }
        catch (OperationCanceledException) { State = WorkflowState.Empty; Status = "Cancelled. Choose an image whenever you're ready."; }
        catch (Exception ex) { SetError("We couldn't remove the background. Try another image or try again.", ex); }
    }
    private void SetError(string message, Exception? ex) { State = WorkflowState.Error; Status = message; ErrorDetails = ex?.ToString() ?? ""; }
    private async Task CopyAsync() { if (_result is null) return; try { await _clipboard.CopyAsync(_result, CancellationToken.None); Status = "Copied the transparent image to your clipboard."; } catch (Exception ex) { SetError("Copying didn't work. You can still use Save as…", ex); } }
    private async Task SaveAsync() => await ExportAsync(null);
    private async Task SaveAsAsync() => await ExportAsync(_desktop.FileDialogs.PickSavePath(Path.GetFileNameWithoutExtension(FileName) + "-background-removed.png"));
    private async Task ExportAsync(string? requestedPath) { if (_result is null) return; try { var file = await _export.ExecuteAsync(_result, Path.GetDirectoryName(_sourcePath), requestedPath); Status = $"Saved a transparent PNG: {Path.GetFileName(file.Path)}"; } catch (Exception ex) { SetError("We couldn't save the image. Check the folder and try again.", ex); } }
    private void Reset() { _cancellation?.Cancel(); _result = null; _sourcePath = null; Before = After = null; Progress = 0; ErrorDetails = ""; Status = "Drop an image here, or choose one to begin."; State = WorkflowState.Empty; Raise(nameof(FileName)); }
    private void RefreshCommands() { CopyCommand.Refresh(); SaveCommand.Refresh(); SaveAsCommand.Refresh(); CancelCommand.Refresh(); NewImageCommand.Refresh(); RetryCommand.Refresh(); }
    public void Dispose() => _cancellation?.Dispose();

}
