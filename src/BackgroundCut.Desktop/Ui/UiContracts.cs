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

/// <summary>
/// Reads image content off the system clipboard for Ctrl+V paste. Implementations must return an
/// empty list (never null) when the clipboard holds no image content, and may throw when the read
/// itself fails (e.g. the clipboard is locked by another process) — callers are expected to catch.
/// A file-drop clipboard payload (e.g. a file copied in Explorer/Finder) should be returned as its
/// original path with no temp file created; a raw bitmap payload must be materialised to a file on
/// disk (in a supported image format/extension) before its path is returned, since everything
/// downstream of MainViewModel.EnqueuePaths is path-based.
/// </summary>
public interface IClipboardImageSource
{
    Task<IReadOnlyList<string>> ReadImagePathsAsync(CancellationToken cancellationToken);
}

public interface IDesktopServices
{
    IFileDialogService FileDialogs { get; }
    IPreviewBitmapFactory Preview { get; }
    IClipboardImageSource ClipboardImages { get; }
    Task OpenSettingsAsync();
}
