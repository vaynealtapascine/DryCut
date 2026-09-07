using System.IO;
using System.Net.Http;
using System.Windows;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Desktop.Ui;
using BackgroundCut.Infrastructure;

namespace BackgroundCut.Desktop;

public partial class App : System.Windows.Application, IDisposable
{
    private SingleInstanceCoordinator? _instance;
    private OnnxBackgroundRemovalEngine? _engine;
    private HttpClient? _httpClient;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                "BackgroundCut ran into an unexpected problem. Your original image was not changed.\n\n" + args.Exception.Message,
                "BackgroundCut",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        _instance = new SingleInstanceCoordinator();
        var requested = e.Args.Length == 1 ? e.Args[0] : null;
        if (!_instance.IsFirstInstance)
        {
            if (requested is not null) SingleInstanceCoordinator.TryHandoff(requested);
            Shutdown();
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BackgroundCut/0.1");
        var downloader = new ModelDownloader(_httpClient);
        var executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BackgroundCut.Desktop.exe");
        var explorer = new WindowsExplorerIntegration(executablePath);
        var services = new DesktopServices(settings, catalog, downloader, explorer);
        var history = new FileProcessedImageHistoryStore();
        _engine = new OnnxBackgroundRemovalEngine();
        _viewModel = new MainViewModel(
            new RemoveBackgroundUseCase(_engine, catalog),
            new ExportImageUseCase(new PngExportService(), settings),
            new WpfClipboardService(),
            settings,
            services,
            history);

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        _instance.PathReceived += (_, path) => window.Dispatcher.Invoke(() =>
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            _viewModel.DropPath(path);
        });
        window.Show();
        if (requested is not null) _viewModel.DropPath(requested);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        _viewModel?.Dispose();
        _engine?.Dispose();
        _httpClient?.Dispose();
        _instance?.Dispose();
        GC.SuppressFinalize(this);
    }
}
