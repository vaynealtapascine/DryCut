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
    // Full mode's window size before switching to panel mode, so switching back restores
    // something sensible instead of leaving the window at the narrow panel width. Seeded with the
    // XAML startup defaults (Width="1320" Height="840") in case the user is already in panel mode
    // on this launch (persisted from a previous session) and never sees full mode at all.
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

    // MinWidth="1024" (the full-mode constraint declared in XAML) makes a narrow panel
    // impossible, so switching modes adjusts the window's size constraints in code. Order
    // matters: a smaller MinWidth must be set before shrinking Width (or Avalonia clamps the
    // resize back up to the old, larger MinWidth), and MaxWidth must be raised before widening
    // Width back on the way to full mode (or the resize is clamped back down).
    private void ApplyViewMode(bool panelMode)
    {
        if (_viewModel is null) return;
        _applyingViewMode = true;
        try
        {
            if (panelMode)
            {
                // Window.Width/Height read back as NaN when the user has resized the window by
                // dragging rather than the size having been set programmatically. Restoring NaN
                // later would hand the window an auto-size instead of the size it actually had,
                // so fall back to the measured client size, then to the remembered default.
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

    // Lets the user resize the panel and have that width remembered/persisted for next time.
    // ClientSizeProperty (rather than Resized/SizeChanged) is used because it is a plain Avalonia
    // property and so participates in the ordinary AvaloniaObject.PropertyChanged pipeline already
    // wired up for view-mode changes below.
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
