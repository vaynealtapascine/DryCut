using BackgroundCut.Application.Ports;
using System;
using System.IO;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Desktop;

public sealed class SettingsViewModel : Ui.ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly Ui.IFileDialogService _dialogs;
    private readonly Action _close;
    private ExportPolicy _policy;
    private string _folder;
    private bool _explorer;
    public SettingsViewModel(ISettingsStore store, Ui.IFileDialogService dialogs, Action close, ExportSettings initial)
    { _store = store; _dialogs = dialogs; _close = close; _policy = initial.Policy; _folder = initial.DefaultFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BackgroundCut"); }
    public ExportPolicy Policy { get => _policy; set { if (Set(ref _policy, value)) Raise(nameof(PolicyDescription)); } }
    public string Folder { get => _folder; set => Set(ref _folder, value); }
    public bool ExplorerIntegration { get => _explorer; set => Set(ref _explorer, value); }
    public static Array Policies => Enum.GetValues<ExportPolicy>();
    public string PolicyDescription => Policy switch { ExportPolicy.SourceFolder => "Keep the result beside the original image.", ExportPolicy.AskEveryTime => "Choose a folder each time you save.", _ => "Save to your BackgroundCut folder." };
    public Ui.RelayCommand BrowseCommand => new(_ => Folder = _dialogs.PickFolder(Folder) ?? Folder);
    public Ui.AsyncCommand SaveCommand => new(async _ => { await _store.SaveExportSettingsAsync(new ExportSettings(Policy, Folder), CancellationToken.None); _close(); });
    public Ui.RelayCommand CancelCommand => new(_ => _close());
}
