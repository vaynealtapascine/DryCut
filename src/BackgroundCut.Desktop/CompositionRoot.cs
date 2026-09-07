using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Forms = System.Windows.Forms;

namespace BackgroundCut.Desktop;

internal sealed class WpfFileDialogs : Ui.IFileDialogService
{
    public string? PickImage() { var d = new WpfOpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff", Title = "Choose an image" }; return d.ShowDialog() == true ? d.FileName : null; }
    public string? PickSavePath(string suggestedName) { var d = new WpfSaveFileDialog { Filter = "PNG image|*.png", FileName = suggestedName, Title = "Save transparent image" }; return d.ShowDialog() == true ? d.FileName : null; }
    public string? PickFolder(string? currentFolder) { using var d = new Forms.FolderBrowserDialog { SelectedPath = currentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Description = "Choose your BackgroundCut folder" }; return d.ShowDialog() == Forms.DialogResult.OK ? d.SelectedPath : null; }
}

internal sealed class WpfPreviewBitmapFactory : Ui.IPreviewBitmapFactory
{
    public BitmapSource? FromFile(string path) { try { var image = new BitmapImage(new Uri(path)) { CacheOption = BitmapCacheOption.OnLoad }; image.Freeze(); return image; } catch { return null; } }
    public BitmapSource FromRgba(ProcessedImage image) { var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Width * 4); bitmap.Freeze(); return bitmap; }
}

internal sealed class DesktopServices : Ui.IDesktopServices
{
    public Ui.IFileDialogService FileDialogs { get; } = new WpfFileDialogs();
    public Ui.IPreviewBitmapFactory Preview { get; } = new WpfPreviewBitmapFactory();
    public async Task OpenSettingsAsync(Window owner)
    {
        var store = new DesktopSettingsStore();
        var settings = await store.LoadExportSettingsAsync(CancellationToken.None);
        var dialog = new SettingsWindow();
        dialog.Owner = owner;
        dialog.DataContext = new SettingsViewModel(store, FileDialogs, dialog.Close, settings);
        dialog.ShowDialog();
    }
    public void ShowMessage(Window owner, string message, string title) => System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}

internal sealed class UnavailableEngine : IBackgroundRemovalEngine
{ public Task<ProcessedImage> ProcessAsync(ImageInput input, ModelDescriptor model, RefinementSettings refinement, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("The image engine is not wired yet. Add the Infrastructure implementation at the composition root."); }
internal sealed class UnavailableCatalog : IModelCatalog
{ public ModelDescriptor Resolve(ModelKind kind) => new(kind, kind.ToString(), kind == ModelKind.FastAndAccurate ? "Fast & accurate" : "Highest quality", kind == ModelKind.FastAndAccurate, false); }
internal sealed class UnavailableExport : IExportService
{ public Task<ExportedFile> ExportAsync(ProcessedImage image, ExportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("The export adapter is not wired yet. Add the Infrastructure implementation at the composition root."); }
internal sealed class UnavailableClipboard : IClipboardService
{ public Task CopyAsync(ProcessedImage image, CancellationToken cancellationToken) => throw new InvalidOperationException("The clipboard adapter is not wired yet. Add the Infrastructure implementation at the composition root."); }
internal sealed class DesktopSettingsStore : ISettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackgroundCut", "settings.json");
    public async Task<ExportSettings> LoadExportSettingsAsync(CancellationToken cancellationToken) { if (!File.Exists(_path)) return ExportSettings.Default; try { var json = await File.ReadAllTextAsync(_path, cancellationToken); return System.Text.Json.JsonSerializer.Deserialize<ExportSettings>(json) ?? ExportSettings.Default; } catch (JsonException) { return ExportSettings.Default; } }
    public async Task SaveExportSettingsAsync(ExportSettings settings, CancellationToken cancellationToken) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temp = _path + ".tmp"; await File.WriteAllTextAsync(temp, System.Text.Json.JsonSerializer.Serialize(settings), cancellationToken); File.Move(temp, _path, true); }
}
