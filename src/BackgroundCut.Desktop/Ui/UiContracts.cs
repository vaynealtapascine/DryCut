using System.Windows;
using System.Windows.Media.Imaging;
using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Desktop.Ui;

public sealed record Choice<T>(T Value, string Name);

public interface IFileDialogService
{
    IReadOnlyList<string> PickImages();
    string? PickSavePath(string suggestedName);
    string? PickFolder(string? currentFolder);
}

public interface IPreviewBitmapFactory
{
    BitmapSource FromRgba(ProcessedImage image);
    BitmapSource? FromFile(string path, int decodePixelWidth = 0);
}

public interface IDesktopServices
{
    IFileDialogService FileDialogs { get; }
    IPreviewBitmapFactory Preview { get; }
    Task OpenSettingsAsync(Window owner);
    void ShowMessage(Window owner, string message, string title);
}
