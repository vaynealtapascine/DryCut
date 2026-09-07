using System.Windows;
using System.Windows.Media.Imaging;
using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Desktop.Ui;

public enum WorkflowState { Empty, Working, Result, Error }

public sealed record Choice<T>(T Value, string Name);

public interface IFileDialogService
{
    string? PickImage();
    string? PickSavePath(string suggestedName);
    string? PickFolder(string? currentFolder);
}

public interface IPreviewBitmapFactory
{
    BitmapSource FromRgba(ProcessedImage image);
    BitmapSource? FromFile(string path);
}

public interface IDesktopServices
{
    IFileDialogService FileDialogs { get; }
    IPreviewBitmapFactory Preview { get; }
    Task OpenSettingsAsync(Window owner);
    void ShowMessage(Window owner, string message, string title);
}
