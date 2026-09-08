using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DryCut.Desktop.Ui;

namespace DryCut.Desktop;

public partial class MainWindow : Window
{
    // Seeded with the XAML startup defaults, since a session that opens straight into panel mode
    // never observes a full-mode size to remember.
    private const double PanelMinWidth = 340;
    private const double PanelMaxWidth = 560;
    private const double PanelMinHeight = 520;
    private const double FullMinWidth = 1024;
    private const double FullMinHeight = 680;
    private double _rememberedFullWidth = 1320;
    private double _rememberedFullHeight = 840;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        DataContextChanged += OnDataContextChanged;
        Resized += OnWindowResized;

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

        if (panelMode)
        {
            // Width/Height read back as NaN once the user has dragged the window, which would
            // otherwise restore an auto-size instead of the size the window actually had.
            _rememberedFullWidth = FirstReal(Width, ClientSize.Width, _rememberedFullWidth);
            _rememberedFullHeight = FirstReal(Height, ClientSize.Height, _rememberedFullHeight);
            MinWidth = PanelMinWidth;
            MinHeight = PanelMinHeight;
            MaxWidth = PanelMaxWidth;
            Width = Math.Clamp(_viewModel.PanelWidth, MinWidth, MaxWidth);
        }
        else
        {
            MinWidth = FullMinWidth;
            MinHeight = FullMinHeight;
            MaxWidth = double.PositiveInfinity;
            Width = _rememberedFullWidth;
            Height = _rememberedFullHeight;
        }
    }

    private static double FirstReal(params double[] candidates)
    {
        foreach (var value in candidates)
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
                return value;
        return 0;
    }

    // Only a user-driven resize may redefine the panel width. ApplyViewMode's own resize also
    // reports a client size, and it differs slightly from the width that was requested, so
    // recording it shrank the panel on every mode switch until it stuck at MinWidth.
    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        if (e.Reason != WindowResizeReason.User || _viewModel is not { IsPanelMode: true }) return;
        _viewModel.PanelWidth = e.ClientSize.Width;
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
