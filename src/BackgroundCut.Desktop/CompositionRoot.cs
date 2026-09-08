using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;
using BackgroundCut.Infrastructure;
using Microsoft.Win32;

namespace BackgroundCut.Desktop;

internal sealed class AvaloniaFileDialogs(Func<Window?> ownerProvider) : Ui.IFileDialogService
{
    private static readonly FilePickerFileType ImageFileType = new("Images")
    {
        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.tif", "*.tiff"]
    };

    private static readonly FilePickerFileType PngFileType = new("PNG image")
    {
        Patterns = ["*.png"],
        MimeTypes = ["image/png"]
    };

    public async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        var provider = GetStorageProvider();
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose images",
            AllowMultiple = true,
            FileTypeFilter = [ImageFileType]
        });
        return files.Select(ToLocalPath).Where(path => path is not null).Cast<string>().ToArray();
    }

    public async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var file = await GetStorageProvider().SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save transparent image",
            SuggestedFileName = suggestedName,
            DefaultExtension = "png",
            FileTypeChoices = [PngFileType]
        });
        return file is null ? null : ToLocalPath(file);
    }

    public async Task<string?> PickFolderAsync(string? currentFolder)
    {
        var provider = GetStorageProvider();
        IStorageFolder? suggested = null;
        if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            suggested = await provider.TryGetFolderFromPathAsync(currentFolder);

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your BackgroundCut folder",
            AllowMultiple = false,
            SuggestedStartLocation = suggested
        });
        return folders.Count == 0 ? null : ToLocalPath(folders[0]);
    }

    private IStorageProvider GetStorageProvider()
    {
        var owner = ownerProvider() ?? throw new InvalidOperationException("The main window is not available.");
        return owner.StorageProvider;
    }

    private static string? ToLocalPath(IStorageItem item) => item.Path.IsFile ? item.Path.LocalPath : null;
}

internal sealed class AvaloniaPreviewBitmapFactory : Ui.IPreviewBitmapFactory
{
    public Bitmap? FromFile(string path, int decodePixelWidth = 0)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return decodePixelWidth > 0 ? Bitmap.DecodeToWidth(stream, decodePixelWidth) : new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public Bitmap FromRgba(ProcessedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var size = new PixelSize(image.Width, image.Height);
        // AlphaFormat.Unpremul: ProcessedImage.Pixels holds straight (non-premultiplied) RGBA,
        // the same representation the old WPF code fed into PixelFormats.Bgra32 (also straight
        // alpha). Using Premul here would darken translucent pixels instead of reproducing them.
        var writeable = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = writeable.Lock();

        var rgba = image.Pixels;
        var srcStride = image.Width * 4;
        var rowBytes = framebuffer.RowBytes;
        var row = new byte[rowBytes];
        for (var y = 0; y < image.Height; y++)
        {
            var srcOffset = y * srcStride;
            for (var x = 0; x < image.Width; x++)
            {
                var si = srcOffset + x * 4;
                var di = x * 4;
                row[di] = rgba[si + 2];     // B
                row[di + 1] = rgba[si + 1]; // G
                row[di + 2] = rgba[si];     // R
                row[di + 3] = rgba[si + 3]; // A
            }
            Marshal.Copy(row, 0, framebuffer.Address + y * rowBytes, rowBytes);
        }

        return writeable;
    }
}

internal sealed class AvaloniaClipboardService(
    Func<Window?> ownerProvider,
    Ui.IPreviewBitmapFactory previewFactory) : IClipboardService
{
    // A "platform" format (as opposed to an "application" format) is written to the OS clipboard
    // under its identifier as-is, with no Avalonia-internal prefix — so this registers the same
    // literal "PNG" clipboard format name the old WPF build wrote via DataObject.SetData("PNG", ...),
    // which GIMP, Krita, browsers and Office all read to recover real alpha. Confirmed by reading
    // Avalonia 11.3.20's Win32 clipboard backend: platform byte[] formats are written to the native
    // IDataObject as raw, unwrapped bytes in an HGLOBAL under a RegisterClipboardFormat(name) id.
    private static readonly DataFormat<byte[]> PngClipboardFormat = DataFormat.CreateBytesPlatformFormat("PNG");

    public async Task CopyAsync(ProcessedImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = ownerProvider() ?? throw new InvalidOperationException("The main window is not available.");
        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard
            ?? throw new InvalidOperationException("The system clipboard is not available.");

        using var bitmap = previewFactory.FromRgba(image);
        using var pngStream = new MemoryStream();
        await ImageSharpImageService.SavePngAsync(image, pngStream, cancellationToken);

        // Put both flavors on the same clipboard item, mirroring the old WpfClipboardService,
        // which set DataObject.SetData("PNG", ...) alongside SetImage(...) on one DataObject:
        // the "PNG" entry carries real alpha for consumers that understand it, and the bitmap
        // entry is a DIB fallback for consumers that only understand plain images.
        using var dataTransfer = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(DataFormat.Bitmap, bitmap);
        item.Set(PngClipboardFormat, pngStream.ToArray());
        dataTransfer.Add(item);

        await clipboard.SetDataAsync(dataTransfer);

        // The old WPF call was Clipboard.SetDataObject(data, copy: true) - the `copy` flag is what
        // pushed the payload to the OS so it outlived the DataObject and the process. SetDataAsync
        // alone leaves this process as the clipboard owner serving the data, so without a flush the
        // content dies when the DataTransfer and bitmap are disposed below (or when BackgroundCut
        // exits) and the user's copy silently pastes nothing. FlushAsync restores that parity.
        await clipboard.FlushAsync();
    }
}

internal sealed class PlatformExplorerIntegration(string executablePath) : IExplorerIntegration
{
    private const string VerbPath = @"Software\Classes\SystemFileAssociations\image\shell\BackgroundCut";
    private readonly string _executablePath = Path.GetFullPath(executablePath);

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.FromResult(false);
        using var command = Registry.CurrentUser.OpenSubKey(VerbPath + @"\command", writable: false);
        return Task.FromResult(command?.GetValue(null) is string value && value.Contains(_executablePath, StringComparison.OrdinalIgnoreCase));
    }

    public Task EnableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Explorer integration is available on Windows only.");
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
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        Registry.CurrentUser.DeleteSubKeyTree(VerbPath, throwOnMissingSubKey: false);
        return Task.CompletedTask;
    }
}

internal sealed class DesktopServices : Ui.IDesktopServices
{
    private readonly Func<Window?> _ownerProvider;
    private readonly ISettingsStore _settings;
    private readonly FileModelCatalog _catalog;
    private readonly ModelDownloader _downloader;
    private readonly IExplorerIntegration _explorer;

    public DesktopServices(
        Func<Window?> ownerProvider,
        ISettingsStore settings,
        FileModelCatalog catalog,
        ModelDownloader downloader,
        IExplorerIntegration explorer)
    {
        _ownerProvider = ownerProvider;
        _settings = settings;
        _catalog = catalog;
        _downloader = downloader;
        _explorer = explorer;
        FileDialogs = new AvaloniaFileDialogs(ownerProvider);
        Preview = new AvaloniaPreviewBitmapFactory();
    }

    public Ui.IFileDialogService FileDialogs { get; }
    public Ui.IPreviewBitmapFactory Preview { get; }

    public async Task OpenSettingsAsync()
    {
        var owner = _ownerProvider() ?? throw new InvalidOperationException("The main window is not available.");
        var initial = await _settings.LoadExportSettingsAsync(CancellationToken.None);
        var dialog = new SettingsWindow();
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
        await dialog.ShowDialog(owner);
    }
}
