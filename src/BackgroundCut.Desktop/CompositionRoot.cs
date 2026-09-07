using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;
using BackgroundCut.Infrastructure;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace BackgroundCut.Desktop;

internal sealed class WpfFileDialogs : Ui.IFileDialogService
{
    public string? PickImage()
    {
        var dialog = new WpfOpenFileDialog
        {
            Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff",
            Title = "Choose an image",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSavePath(string suggestedName)
    {
        var dialog = new WpfSaveFileDialog
        {
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = suggestedName,
            Title = "Save transparent image"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? currentFolder)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            SelectedPath = currentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Description = "Choose your BackgroundCut folder",
            UseDescriptionForTitle = true
        };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}

internal sealed class WpfPreviewBitmapFactory : Ui.IPreviewBitmapFactory
{
    public BitmapSource? FromFile(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public BitmapSource FromRgba(ProcessedImage image)
    {
        var bgra = ConvertRgbaToBgra(image.Pixels);
        var bitmap = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    internal static byte[] ConvertRgbaToBgra(byte[] rgba)
    {
        var bgra = (byte[])rgba.Clone();
        for (var i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }
}

internal sealed class WpfClipboardService : IClipboardService
{
    public async Task CopyAsync(ProcessedImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        await using var png = new MemoryStream();
        await ImageSharpImageService.SavePngAsync(image, png, cancellationToken).ConfigureAwait(false);
        var pngBytes = png.ToArray();
        var bgra = WpfPreviewBitmapFactory.ConvertRgbaToBgra(image.Pixels);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Width * 4);
            bitmap.Freeze();
            var data = new System.Windows.DataObject();
            data.SetData("PNG", new MemoryStream(pngBytes, writable: false));
            data.SetImage(bitmap);
            System.Windows.Clipboard.SetDataObject(data, true);
        });
    }
}

internal sealed class WindowsExplorerIntegration : IExplorerIntegration
{
    private const string VerbPath = @"Software\Classes\SystemFileAssociations\image\shell\BackgroundCut";
    private readonly string _executablePath;

    public WindowsExplorerIntegration(string executablePath) => _executablePath = Path.GetFullPath(executablePath);

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = Registry.CurrentUser.OpenSubKey(VerbPath + @"\command", writable: false);
        return Task.FromResult(command?.GetValue(null) is string value && value.Contains(_executablePath, StringComparison.OrdinalIgnoreCase));
    }

    public Task EnableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var verb = Registry.CurrentUser.CreateSubKey(VerbPath, writable: true)
            ?? throw new InvalidOperationException("Windows did not allow the Explorer menu entry to be created.");
        verb.SetValue(null, "Remove background with BackgroundCut", RegistryValueKind.String);
        verb.SetValue("Icon", $"\"{_executablePath}\"", RegistryValueKind.String);
        using var command = verb.CreateSubKey("command", writable: true)
            ?? throw new InvalidOperationException("Windows did not allow the Explorer command to be created.");
        command.SetValue(null, $"\"{_executablePath}\" \"%1\"", RegistryValueKind.String);
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Registry.CurrentUser.DeleteSubKeyTree(VerbPath, throwOnMissingSubKey: false);
        return Task.CompletedTask;
    }
}

internal sealed class DesktopServices : Ui.IDesktopServices
{
    private readonly ISettingsStore _settings;
    private readonly FileModelCatalog _catalog;
    private readonly ModelDownloader _downloader;
    private readonly IExplorerIntegration _explorer;

    public DesktopServices(ISettingsStore settings, FileModelCatalog catalog, ModelDownloader downloader, IExplorerIntegration explorer)
    {
        _settings = settings;
        _catalog = catalog;
        _downloader = downloader;
        _explorer = explorer;
    }

    public Ui.IFileDialogService FileDialogs { get; } = new WpfFileDialogs();
    public Ui.IPreviewBitmapFactory Preview { get; } = new WpfPreviewBitmapFactory();

    public async Task OpenSettingsAsync(Window owner)
    {
        var initial = await _settings.LoadExportSettingsAsync(CancellationToken.None);
        var dialog = new SettingsWindow { Owner = owner };
        var viewModel = new SettingsViewModel(
            _settings,
            FileDialogs,
            _explorer,
            _catalog,
            _downloader,
            dialog.Close,
            initial);
        dialog.DataContext = viewModel;
        await viewModel.InitializeAsync();
        dialog.ShowDialog();
    }

    public void ShowMessage(Window owner, string message, string title) =>
        System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
