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
    private bool _handlingDispatcherException;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _instance = new SingleInstanceCoordinator();
        var requested = e.Args;
        if (!_instance.IsFirstInstance)
        {
            foreach (var path in requested) SingleInstanceCoordinator.TryHandoff(path);
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
        _instance.PathReceived += (_, path) =>
        {
            var dispatcher = window.Dispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            try
            {
                dispatcher.Invoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;
                    _viewModel.DropPath(path);
                });
            }
            catch (Exception)
            {
                // The app is shutting down and the dispatcher is no longer accepting work
                // (the exact exception type varies with shutdown timing). This handler runs
                // on a threadpool thread with nothing to report to, so we just drop the request.
            }
        };
        window.Show();
        if (requested.Length > 0) _viewModel.DropPaths(requested);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        Console.Error.WriteLine(args.Exception);
        if (_handlingDispatcherException)
        {
            args.Handled = false;
            return;
        }

        _handlingDispatcherException = true;
        try
        {
            System.Windows.MessageBox.Show(
                "BackgroundCut ran into an unexpected problem. Your original image was not changed.\n\n" + args.Exception.Message,
                "BackgroundCut",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        }
        catch
        {
            args.Handled = false;
        }
        finally
        {
            _handlingDispatcherException = false;
        }
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
