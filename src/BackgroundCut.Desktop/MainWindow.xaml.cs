using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using BackgroundCut.Desktop.Ui;

namespace BackgroundCut.Desktop;

public partial class MainWindow : Window
{
    // Seeded with the XAML startup defaults, since a session that opens straight into panel mode
    // never observes a full-mode size to remember.
    private double _rememberedFullWidth = 1320;
    private double _rememberedFullHeight = 840;
    private bool _applyingViewMode;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnWindowPropertyChanged;

        // Wired here rather than a <Window.KeyBindings> entry in MainWindow.xaml: this app owns no
        // Ctrl+V TextBox editing of its own (Settings' TextBox handles its own paste via the normal
        // text-input gesture), so a global KeyBindings entry would fire even while the user is
        // typing there. Tunnelling lets us check focus first and simply not handle the event when a
        // TextBox has it, leaving the box's own paste behavior untouched.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || e.KeyModifiers != KeyModifiers.Control && e.KeyModifiers != KeyModifiers.Meta) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (DataContext is not MainViewModel viewModel) return;

        e.Handled = true;
        viewModel.PasteCommand.Execute(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (DataContext is MainViewModel viewModel)
            await Task.WhenAll(viewModel.InitializeAsync(), viewModel.LoadUiSettingsAsync());
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPanelMode) && _viewModel is not null)
            ApplyViewMode(_viewModel.IsPanelMode);
    }

    // Order matters: MinWidth must be lowered before Width is shrunk, and MaxWidth raised before
    // Width is widened, or Avalonia clamps the resize back to the previous constraint.
    private void ApplyViewMode(bool panelMode)
    {
        if (_viewModel is null) return;
        _applyingViewMode = true;
        try
        {
            if (panelMode)
            {
                // Width/Height read back as NaN once the user has dragged the window, which would
                // later restore an auto-size instead of the size it actually had.
                _rememberedFullWidth = FirstReal(Width, ClientSize.Width, _rememberedFullWidth);
                _rememberedFullHeight = FirstReal(Height, ClientSize.Height, _rememberedFullHeight);
                MinWidth = 340;
                MinHeight = 520;
                MaxWidth = 560;
                Width = Math.Clamp(_viewModel.PanelWidth, MinWidth, MaxWidth);
            }
            else
            {
                MinWidth = 1024;
                MinHeight = 680;
                MaxWidth = double.PositiveInfinity;
                Width = _rememberedFullWidth;
                Height = _rememberedFullHeight;
            }
        }
        finally
        {
            _applyingViewMode = false;
        }
    }

    private static double FirstReal(params double[] candidates)
    {
        foreach (var value in candidates)
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
                return value;
        return 0;
    }

    // ClientSizeProperty rather than Resized: it is a plain Avalonia property, so it rides the
    // PropertyChanged pipeline already wired up here.
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_applyingViewMode || e.Property != ClientSizeProperty || _viewModel is not { IsPanelMode: true }) return;
        _viewModel.PanelWidth = ClientSize.Width;
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
