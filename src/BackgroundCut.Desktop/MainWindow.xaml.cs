using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using BackgroundCut.Desktop.Ui;

namespace BackgroundCut.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (DataContext is MainViewModel viewModel)
            await viewModel.InitializeAsync();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.Items
            .Select(item => item.TryGetFile())
            .Where(file => file is not null)
            .Select(file => file!.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (paths is { Length: > 0 } && DataContext is MainViewModel viewModel)
            viewModel.DropPaths(paths);
    }
}
