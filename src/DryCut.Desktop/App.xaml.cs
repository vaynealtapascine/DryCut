using System.IO;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using DryCut.Application.UseCases;
using DryCut.Desktop.Ui;
using DryCut.Domain.Models;
using DryCut.Infrastructure;

namespace DryCut.Desktop;

public partial class App : Avalonia.Application, IDisposable
{
    private SingleInstanceCoordinator? _instance;
    private OnnxBackgroundRemovalEngine? _engine;
    private HttpClient? _httpClient;
    private MainViewModel? _viewModel;
    private bool _disposed;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _instance = new SingleInstanceCoordinator();
        var requested = desktop.Args ?? [];
        if (!_instance.IsFirstInstance)
        {
            foreach (var path in requested) SingleInstanceCoordinator.TryHandoff(path);
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var settings = new JsonSettingsStore();
        ApplyPersistedTheme(settings);
        var bundledModels = Path.Combine(AppContext.BaseDirectory, "models");
        var userModels = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DryCut",
            "models");
        var catalog = new FileModelCatalog(bundledModels, userModels);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DryCut/1.0");
        var downloader = new ModelDownloader(_httpClient);
        var executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DryCut.Desktop");
        var explorer = new PlatformExplorerIntegration(executablePath);
        var history = new FileProcessedImageHistoryStore();
        _engine = new OnnxBackgroundRemovalEngine();

        MainWindow? window = null;
        var services = new DesktopServices(() => window, settings, catalog, downloader, explorer);
        _viewModel = new MainViewModel(
            new RemoveBackgroundUseCase(_engine, catalog),
            new ExportImageUseCase(new PngExportService(), settings),
            new AvaloniaClipboardService(() => window, services.Preview),
            settings,
            services,
            history);

        window = new MainWindow { DataContext = _viewModel };
        desktop.MainWindow = window;
        desktop.Exit += OnExit;
        _instance.PathReceived += (_, path) => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || window is null) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            if (!window.IsVisible) window.Show();
            window.Activate();
            _viewModel.DropPath(path);
        });

        if (requested.Length > 0) _viewModel.DropPaths(requested);
        base.OnFrameworkInitializationCompleted();
    }

    // Applied synchronously (not fire-and-forget) so the persisted preference is in place before
    // MainWindow is constructed below — otherwise the window would flash the App.axaml compiled
    // default (Dark) for a frame before switching. JsonSettingsStore's async methods all use
    // ConfigureAwait(false) internally, so blocking on them here, before the dispatcher loop has
    // started, does not risk a deadlock.
    private static void ApplyPersistedTheme(JsonSettingsStore settings)
    {
        try
        {
            var uiSettings = settings.LoadUiSettingsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Current!.RequestedThemeVariant = ToThemeVariant(uiSettings.EffectiveTheme);
        }
        catch
        {
            // Theme preference is supplementary; the compiled App.axaml default (Dark) keeps the
            // app usable and matches the deliberate product default for a first run anyway.
        }
    }

    private static ThemeVariant ToThemeVariant(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ThemeVariant.Light,
        ThemeMode.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel?.Dispose();
        _engine?.Dispose();
        _httpClient?.Dispose();
        _instance?.Dispose();
        GC.SuppressFinalize(this);
    }
}
