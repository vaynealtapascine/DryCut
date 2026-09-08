using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DryCut.Desktop;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Covers every way this dialog can close (Cancel, titlebar X, Alt+F4) with one path:
        // if Save never committed a new theme, put back whatever was active when we opened.
        Closing += (_, _) =>
        {
            if (DataContext is SettingsViewModel viewModel) viewModel.RestoreThemeIfNotCommitted();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
