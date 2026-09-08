using System.IO;
using System.Net.Http;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using BackgroundCut.Application.Ports;
using BackgroundCut.Desktop.Ui;
using BackgroundCut.Domain.Models;
using BackgroundCut.Infrastructure;
using Xunit;

namespace BackgroundCut.Desktop.Tests;

/// <summary>
/// Covers the live theme preview added to SettingsViewModel: changing Theme applies immediately
/// (Application.Current.RequestedThemeVariant), Save persists it, and Cancel (or any other way of
/// closing the window without saving) restores whatever variant was active when it opened.
/// </summary>
public sealed class SettingsViewModelTests
{
    [AvaloniaFact]
    public void ChangingThemeAppliesLivePreviewImmediately()
    {
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var directory = TempModelDirectory();
        var vm = CreateViewModel(directory.FullName, new UiSettings(Theme: ThemeMode.Dark));

        vm.Theme = ThemeMode.Light;

        Assert.Equal(ThemeVariant.Light, Avalonia.Application.Current.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void CancellingRestoresTheVariantThatWasActiveWhenTheWindowOpened()
    {
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var directory = TempModelDirectory();
        var vm = CreateViewModel(directory.FullName, new UiSettings(Theme: ThemeMode.Dark));

        vm.Theme = ThemeMode.Light;
        Assert.Equal(ThemeVariant.Light, Avalonia.Application.Current.RequestedThemeVariant);

        // Simulates the window's Closing handler firing for Cancel, the titlebar X, or Alt+F4 --
        // none of which call SaveCommand first.
        vm.RestoreThemeIfNotCommitted();

        Assert.Equal(ThemeVariant.Dark, Avalonia.Application.Current.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void CancellingRestoresSystemWhenThatWasTheOriginalVariant()
    {
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        var directory = TempModelDirectory();
        var vm = CreateViewModel(directory.FullName, new UiSettings(Theme: ThemeMode.System));

        vm.Theme = ThemeMode.Dark;
        Assert.Equal(ThemeVariant.Dark, Avalonia.Application.Current.RequestedThemeVariant);

        vm.RestoreThemeIfNotCommitted();

        Assert.Equal(ThemeVariant.Default, Avalonia.Application.Current.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public async Task SavingCommitsTheThemeSoClosingAfterwardsDoesNotRevertIt()
    {
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var directory = TempModelDirectory();
        var store = new FakeSettingsStore();
        var vm = CreateViewModel(directory.FullName, UiSettings.Default, store);
        await vm.InitializeAsync();

        vm.Theme = ThemeMode.Light;
        vm.SaveCommand.Execute(null);
        await Task.Delay(50); // AsyncCommand.Execute is async void; give the save a moment to finish.

        Assert.Equal(ThemeMode.Light, store.SavedUi?.Theme);

        // Post-save close (the window's Closing handler) must leave the saved theme alone.
        vm.RestoreThemeIfNotCommitted();

        Assert.Equal(ThemeVariant.Light, Avalonia.Application.Current.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public async Task SavingPreservesModePanelWidthAndAlwaysOnTopUnchanged()
    {
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var directory = TempModelDirectory();
        var store = new FakeSettingsStore();
        var existing = new UiSettings(ViewMode.Panel, 444, AlwaysOnTop: true, Theme: ThemeMode.Dark);
        var vm = CreateViewModel(directory.FullName, existing, store);
        await vm.InitializeAsync();

        vm.Theme = ThemeMode.System;
        vm.SaveCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal(ThemeMode.System, store.SavedUi?.Theme);
        Assert.Equal(ViewMode.Panel, store.SavedUi?.Mode);
        Assert.Equal(444, store.SavedUi?.PanelWidth);
        Assert.True(store.SavedUi?.AlwaysOnTop);
    }

    private static SettingsViewModel CreateViewModel(string modelDirectory, UiSettings initialUi, FakeSettingsStore? store = null)
    {
        var catalog = new FileModelCatalog(modelDirectory);
        var downloader = new ModelDownloader(new HttpClient());
        return new SettingsViewModel(
            store ?? new FakeSettingsStore(),
            new FakeFileDialogService(),
            new FakeExplorerIntegration(),
            catalog,
            downloader,
            close: () => { },
            ExportSettings.Default,
            initialUi);
    }

    private static DirectoryInfo TempModelDirectory() =>
        Directory.CreateTempSubdirectory("BackgroundCut-settings-theme-test");

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private ExportSettings _export = ExportSettings.Default;
        public UiSettings? SavedUi { get; private set; }

        public Task<ExportSettings> LoadExportSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(_export);

        public Task SaveExportSettingsAsync(ExportSettings settings, CancellationToken cancellationToken)
        {
            _export = settings;
            return Task.CompletedTask;
        }

        public Task<UiSettings> LoadUiSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(SavedUi ?? UiSettings.Default);

        public Task SaveUiSettingsAsync(UiSettings settings, CancellationToken cancellationToken)
        {
            SavedUi = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileDialogService : IFileDialogService
    {
        public Task<IReadOnlyList<string>> PickImagesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string? currentFolder) => Task.FromResult<string?>(null);
    }

    private sealed class FakeExplorerIntegration : IExplorerIntegration
    {
        public bool IsSupported => false;
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task EnableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
