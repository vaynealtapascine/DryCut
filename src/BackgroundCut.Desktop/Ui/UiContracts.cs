using Avalonia.Media.Imaging;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Desktop.Ui;

public sealed record Choice<T>(T Value, string Name);

public interface IFileDialogService
{
    Task<IReadOnlyList<string>> PickImagesAsync();
    Task<string?> PickSavePathAsync(string suggestedName);
    Task<string?> PickFolderAsync(string? currentFolder);
}

public interface IPreviewBitmapFactory
{
    Bitmap FromRgba(ProcessedImage image);
    Bitmap? FromFile(string path, int decodePixelWidth = 0);
}

public interface IDesktopServices
{
    IFileDialogService FileDialogs { get; }
    IPreviewBitmapFactory Preview { get; }
    Task OpenSettingsAsync();
}
