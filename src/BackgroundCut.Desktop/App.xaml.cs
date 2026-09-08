using System.IO;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Desktop.Ui;
using BackgroundCut.Infrastructure;

namespace BackgroundCut.Desktop;

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
        var bundledModels = Path.Combine(AppContext.BaseDirectory, "models");
        var userModels = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BackgroundCut",
            "models");
        var catalog = new FileModelCatalog(bundledModels, userModels);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BackgroundCut/1.0");
        var downloader = new ModelDownloader(_httpClient);
        var executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BackgroundCut.Desktop");
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
