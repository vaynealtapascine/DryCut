using System.Windows;
using BackgroundCut.Application.UseCases;
using BackgroundCut.Desktop.Ui;

namespace BackgroundCut.Desktop;

public partial class App : System.Windows.Application, IDisposable
{
    private SingleInstanceCoordinator? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); _instance = new();
        var requested = e.Args.Length == 1 ? e.Args[0] : null;
        if (!_instance.IsFirstInstance) { if (requested is not null) SingleInstanceCoordinator.TryHandoff(requested); Shutdown(); return; }
        var services = new DesktopServices();
        var catalog = new UnavailableCatalog();
        var vm = new MainViewModel(new RemoveBackgroundUseCase(new UnavailableEngine(), catalog), new ExportImageUseCase(new UnavailableExport(), new DesktopSettingsStore()), new UnavailableClipboard(), catalog, services);
        var window = new MainWindow { DataContext = vm };
        _instance.PathReceived += (_, path) => window.Dispatcher.Invoke(() => { if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); window.Topmost = true; window.Topmost = false; vm.DropPath(path); });
        window.Show();
        if (requested is not null) vm.DropPath(requested);
    }
    protected override void OnExit(ExitEventArgs e) { Dispose(); base.OnExit(e); }
    public void Dispose() { _instance?.Dispose(); GC.SuppressFinalize(this); }

}
