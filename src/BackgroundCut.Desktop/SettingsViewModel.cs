using System.IO;
using BackgroundCut.Application.Ports;
using BackgroundCut.Desktop.Ui;
using BackgroundCut.Domain.Models;
using BackgroundCut.Infrastructure;

namespace BackgroundCut.Desktop;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IFileDialogService _dialogs;
    private readonly IExplorerIntegration _explorer;
    private readonly FileModelCatalog _catalog;
    private readonly ModelDownloader _downloader;
    private readonly Action _close;
    private ExportPolicy _policy;
    private string _folder;
    private bool _explorerEnabled;
    private bool _highestQualityInstalled;
    private bool _isDownloading;
    private double _downloadProgress;
    private string _modelStatus = "";

    public SettingsViewModel(
        ISettingsStore store,
        IFileDialogService dialogs,
        IExplorerIntegration explorer,
        FileModelCatalog catalog,
        ModelDownloader downloader,
        Action close,
        ExportSettings initial)
    {
        _store = store;
        _dialogs = dialogs;
        _explorer = explorer;
        _catalog = catalog;
        _downloader = downloader;
        _close = close;
        _policy = initial.Policy;
        _folder = initial.DefaultFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BackgroundCut");

        BrowseCommand = new RelayCommand(_ => Browse());
        SaveCommand = new AsyncCommand(_ => SaveAsync(), _ => !IsDownloading);
        CancelCommand = new RelayCommand(_ => _close(), _ => !IsDownloading);
        DownloadModelCommand = new AsyncCommand(_ => DownloadModelAsync(), _ => !IsDownloading && !HighestQualityInstalled);
        RemoveModelCommand = new RelayCommand(_ => RemoveModel(), _ => !IsDownloading && HighestQualityInstalled);
    }

    public ExportPolicy Policy
    {
        get => _policy;
        set
        {
            if (Set(ref _policy, value))
            {
                Raise(nameof(PolicyDescription));
                Raise(nameof(UsesDefaultFolder));
            }
        }
    }

    public string Folder { get => _folder; set => Set(ref _folder, value); }
    public bool ExplorerEnabled { get => _explorerEnabled; set => Set(ref _explorerEnabled, value); }
    public bool UsesDefaultFolder => Policy == ExportPolicy.DefaultFolder;
    public bool HighestQualityInstalled { get => _highestQualityInstalled; private set { if (Set(ref _highestQualityInstalled, value)) RefreshCommands(); } }
    public bool IsDownloading { get => _isDownloading; private set { if (Set(ref _isDownloading, value)) RefreshCommands(); } }
    public double DownloadProgress { get => _downloadProgress; private set => Set(ref _downloadProgress, value); }
    public string ModelStatus { get => _modelStatus; private set => Set(ref _modelStatus, value); }
    public static IReadOnlyList<Choice<ExportPolicy>> PolicyChoices { get; } = new[]
    {
        new Choice<ExportPolicy>(ExportPolicy.DefaultFolder, "My BackgroundCut folder"),
        new Choice<ExportPolicy>(ExportPolicy.SourceFolder, "Beside the original image"),
        new Choice<ExportPolicy>(ExportPolicy.AskEveryTime, "Ask me every time")
    };
    public string PolicyDescription => Policy switch
    {
        ExportPolicy.SourceFolder => "Keep each result beside its original image.",
        ExportPolicy.AskEveryTime => "Choose a location each time you save.",
        _ => "Save automatically to your BackgroundCut folder."
    };

    public RelayCommand BrowseCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand DownloadModelCommand { get; }
    public RelayCommand RemoveModelCommand { get; }

    public async Task InitializeAsync()
    {
        ExplorerEnabled = await _explorer.IsEnabledAsync(CancellationToken.None);
        HighestQualityInstalled = _catalog.Resolve(ModelKind.HighestQuality).IsInstalled;
        ModelStatus = HighestQualityInstalled
            ? "Highest quality is installed and ready."
            : "Optional download: about 467 MB. Images still stay on this PC.";
    }

    private async Task SaveAsync()
    {
        if (Policy == ExportPolicy.DefaultFolder && string.IsNullOrWhiteSpace(Folder))
        {
            ModelStatus = "Choose a default folder before saving settings.";
            return;
        }

        await _store.SaveExportSettingsAsync(new ExportSettings(Policy, Folder), CancellationToken.None);
        if (ExplorerEnabled)
            await _explorer.EnableAsync(CancellationToken.None);
        else
            await _explorer.DisableAsync(CancellationToken.None);
        _close();
    }

    private async Task DownloadModelAsync()
    {
        IsDownloading = true;
        DownloadProgress = 0;
        ModelStatus = "Downloading highest-quality model…";
        try
        {
            var progress = new Progress<ProcessingProgress>(value =>
            {
                DownloadProgress = Math.Clamp(value.Fraction, 0, 1);
                ModelStatus = $"Downloading highest-quality model… {DownloadProgress:P0}";
            });
            await _downloader.DownloadAsync(
                _catalog.GetArtifact(ModelKind.HighestQuality),
                _catalog.UserModelDirectory,
                progress,
                CancellationToken.None);
            HighestQualityInstalled = true;
            DownloadProgress = 1;
            ModelStatus = "Highest quality is installed and ready.";
        }
        catch (Exception ex)
        {
            ModelStatus = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void Browse()
    {
        try
        {
            Folder = _dialogs.PickFolder(Folder) ?? Folder;
        }
        catch (Exception ex)
        {
            ModelStatus = $"Couldn't open the folder picker: {ex.Message}";
        }
    }

    private void RemoveModel()
    {
        try
        {
            var removed = _catalog.RemoveOptionalModel(ModelKind.HighestQuality);
            HighestQualityInstalled = false;
            DownloadProgress = 0;
            ModelStatus = removed
                ? "Highest-quality model removed. Fast & accurate is still ready."
                : "Highest-quality model was already removed. Fast & accurate is still ready.";
        }
        catch (Exception ex)
        {
            // The file may still be in use (e.g. an open inference session) or otherwise
            // inaccessible. Nothing changed, so leave HighestQualityInstalled as-is.
            ModelStatus = $"Couldn't remove the highest-quality model: {ex.Message}";
        }
    }

    private void RefreshCommands()
    {
        SaveCommand.Refresh();
        CancelCommand.Refresh();
        DownloadModelCommand.Refresh();
        RemoveModelCommand.Refresh();
    }
}
