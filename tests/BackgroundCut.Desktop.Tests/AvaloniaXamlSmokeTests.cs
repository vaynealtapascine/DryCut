using System.IO;
using System.Linq;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BackgroundCut.Application.Ports;
using BackgroundCut.Desktop.Ui;
using BackgroundCut.Domain.Models;
using BackgroundCut.Infrastructure;
using Xunit;

namespace BackgroundCut.Desktop.Tests;

public sealed class AvaloniaXamlSmokeTests
{
    [AvaloniaFact]
    public void MainWindowXamlLoads()
    {
        var window = new MainWindow();

        Assert.Equal("BackgroundCut", window.Title);
        Assert.Equal(1024, window.MinWidth);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsWindowXamlLoads()
    {
        var window = new SettingsWindow();

        Assert.Equal("BackgroundCut settings", window.Title);
        Assert.Equal(620, window.Width);
        window.Close();
    }

    // Reflection bindings (AvaloniaUseCompiledBindingsByDefault=false) neither fail the build nor
    // throw on a bad path: they log and leave the target at its default. Avalonia 11.3.20 has no
    // BindingDiagnostics to hook, so these tests capture Logger.Sink, which every binding-error
    // path writes through, and fail on anything logged to LogArea.Binding.
    [AvaloniaFact]
    public async Task MainWindowBindingsResolveWithoutErrors()
    {
        using var vm = await MainViewModelTests.CreateViewModelForBindingTestsAsync();

        var errors = new List<string>();
        using (CaptureBindingErrors(errors))
        {
            var window = new MainWindow { DataContext = vm };
            PrepareForHeadlessLayout(window);
            window.Show();
            PumpDispatcher();
            window.Close();
        }

        Assert.True(errors.Count == 0, FormatBindingFailure("MainWindow", errors));
    }

    // Panel mode's layout is IsVisible="{Binding IsPanelMode}" -- while IsVisible is false its
    // bindings never get a chance to evaluate, so the ordinary MainWindowBindingsResolveWithoutErrors
    // test (which leaves IsFullMode active) would never surface a typo in that half of the XAML.
    // This test switches the real ToggleViewModeCommand so panel mode is the one actually shown.
    [AvaloniaFact]
    public async Task MainWindowPanelModeBindingsResolveWithoutErrors()
    {
        using var vm = await MainViewModelTests.CreateViewModelForBindingTestsAsync();
        vm.ToggleViewModeCommand.Execute(null);
        Assert.True(vm.IsPanelMode);

        var errors = new List<string>();
        using (CaptureBindingErrors(errors))
        {
            var window = new MainWindow { DataContext = vm };
            PrepareForHeadlessLayout(window);
            window.Show();
            PumpDispatcher();
            window.Close();
        }

        Assert.True(errors.Count == 0, FormatBindingFailure("MainWindow (panel mode)", errors));
    }

    [AvaloniaFact]
    public async Task SettingsWindowBindingsResolveWithoutErrors()
    {
        var modelDirectory = Directory.CreateTempSubdirectory("BackgroundCut-settings-binding-test");
        try
        {
            var settingsStore = new FakeSettingsStore();
            var catalog = new FileModelCatalog(modelDirectory.FullName);
            var downloader = new ModelDownloader(new HttpClient());
            var initial = await settingsStore.LoadExportSettingsAsync(CancellationToken.None);
            var initialUi = await settingsStore.LoadUiSettingsAsync(CancellationToken.None);
            var vm = new SettingsViewModel(
                settingsStore,
                new FakeFileDialogService(),
                new FakeExplorerIntegration(),
                catalog,
                downloader,
                close: () => { },
                initial,
                initialUi);
            await vm.InitializeAsync();

            var errors = new List<string>();
            using (CaptureBindingErrors(errors))
            {
                var window = new SettingsWindow { DataContext = vm };
                PrepareForHeadlessLayout(window);
                window.Show();
                PumpDispatcher();
                window.Close();
            }

            Assert.True(errors.Count == 0, FormatBindingFailure("SettingsWindow", errors));
        }
        finally
        {
            modelDirectory.Delete(recursive: true);
        }
    }

    // The queue item's selection and status colours come from theme-scoped style classes rather
    // than hex strings on the view model, so they follow the active theme. Style classes and
    // DynamicResource lookups both fail silently, so assert the realized Border actually resolved
    // its brushes from the active theme rather than merely being non-null.
    [AvaloniaFact]
    public async Task QueueItemColoursResolveFromTheActiveThemeInTheRealVisualTree()
    {
        using var vm = await MainViewModelTests.CreateViewModelForBindingTestsAsync();
        var item = Assert.Single(vm.VisibleItems);

        var window = new MainWindow { DataContext = vm };
        PrepareForHeadlessLayout(window);
        window.Show();
        PumpDispatcher();

        var border = window.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, item));
        Assert.NotNull(border);
        Assert.Contains("queueItem", border!.Classes);
        Assert.Equal(item.IsSelected, border.Classes.Contains("selected"));

        var expectedBorder = ThemeBrush(item.IsSelected ? "PrimaryBrush" : "BorderBrush");
        var expectedBackground = ThemeBrush(item.IsSelected ? "AccentSoftBrush" : "SurfaceBrush");

        var actualBorder = Assert.IsAssignableFrom<ISolidColorBrush>(border.BorderBrush);
        var actualBackground = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
        Assert.Equal(expectedBorder.Color, actualBorder.Color);
        Assert.Equal(expectedBackground.Color, actualBackground.Color);

        window.Close();
    }

    private static ISolidColorBrush ThemeBrush(string key)
    {
        var app = Avalonia.Application.Current;
        Assert.NotNull(app);
        Assert.True(app!.TryGetResource(key, app.ActualThemeVariant, out var value), $"theme resource '{key}' did not resolve");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value);
    }

    // The headless font manager only knows the synthetic "$Default" family, while App.axaml sets
    // Inter on every Window, so any real layout pass throws before these tests reach the tree they
    // care about. A local value on the window outranks the App-level style setter, which fixes it
    // for these tests without touching TestAppBuilder or production XAML.
    private static void PrepareForHeadlessLayout(Window window) => window.FontFamily = FontFamily.Default;

    private static void PumpDispatcher()
    {
        // Headless layout/binding work is queued onto the dispatcher rather than executed
        // synchronously by Show(); running the loop a couple of times drains layout, then the
        // bindings/templates that layout triggers.
        for (var i = 0; i < 3; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static string FormatBindingFailure(string windowName, IReadOnlyList<string> errors) =>
        $"{errors.Count} binding error(s) on {windowName}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";

    private static BindingErrorScope CaptureBindingErrors(List<string> messages) => new(messages);

    private sealed class BindingErrorScope : IDisposable
    {
        private readonly ILogSink? _previous;

        public BindingErrorScope(List<string> messages)
        {
            _previous = Logger.Sink;
            Logger.Sink = new CapturingSink(messages, _previous);
        }

        public void Dispose() => Logger.Sink = _previous;
    }

    private sealed class CapturingSink(List<string> messages, ILogSink? inner) : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) =>
            IsBindingWarning(level, area) || (inner?.IsEnabled(level, area) ?? false);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsBindingWarning(level, area))
                messages.Add($"[{level}] {Describe(source)}: {messageTemplate}");
            inner?.Log(level, area, source, messageTemplate);
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsBindingWarning(level, area))
                messages.Add($"[{level}] {Describe(source)}: {messageTemplate} ({string.Join(", ", propertyValues)})");
            inner?.Log(level, area, source, messageTemplate, propertyValues);
        }

        private static bool IsBindingWarning(LogEventLevel level, string area) =>
            area == LogArea.Binding && level >= LogEventLevel.Warning;

        private static string Describe(object? source) => source switch
        {
            null => "<null>",
            AvaloniaObject avaloniaObject => avaloniaObject.GetType().Name,
            _ => source.ToString() ?? source.GetType().Name
        };
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private ExportSettings _settings = ExportSettings.Default;
        private UiSettings _uiSettings = UiSettings.Default;

        public Task<ExportSettings> LoadExportSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(_settings);

        public Task SaveExportSettingsAsync(ExportSettings settings, CancellationToken cancellationToken)
        {
            _settings = settings;
            return Task.CompletedTask;
        }

        public Task<UiSettings> LoadUiSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(_uiSettings);

        public Task SaveUiSettingsAsync(UiSettings settings, CancellationToken cancellationToken)
        {
            _uiSettings = settings;
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
