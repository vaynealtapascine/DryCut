namespace DryCut.Domain.Models;

public enum ModelKind
{
    FastAndAccurate,
    HighestQuality
}

public enum RefinementPreset
{
    None,
    Soft,
    Balanced,
    Detailed
}

public enum ExportPolicy
{
    DefaultFolder,
    SourceFolder,
    AskEveryTime
}

public enum ViewMode
{
    Full,
    Panel
}

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public sealed record ModelDescriptor(
    ModelKind Kind,
    string Id,
    string DisplayName,
    bool IsBundled,
    bool IsInstalled);

public sealed record RefinementSettings(RefinementPreset Preset = RefinementPreset.Balanced)
{
    public static RefinementSettings Default { get; } = new();
}

public sealed record ExportSettings(
    ExportPolicy Policy = ExportPolicy.DefaultFolder,
    string? DefaultFolder = null)
{
    public static ExportSettings Default { get; } = new();
}

// Theme is nullable rather than defaulting straight to ThemeMode.Dark: MainViewModel's
// SaveUiSettingsAsync round-trips only Mode/PanelWidth/AlwaysOnTop (it has no notion of theme),
// so a non-nullable field would get silently reset to its default every time the user resizes
// the panel or toggles always-on-top, clobbering whatever they picked in Settings. Null means
// "this writer didn't touch theme" and JsonSettingsStore.SaveUiSettingsAsync merges it with
// whatever is already on disk instead of overwriting it. Read EffectiveTheme, not Theme, for the
// actual preference (defaults to Dark, the deliberate product default for a first run).
public sealed record UiSettings(
    ViewMode Mode = ViewMode.Full,
    double PanelWidth = 380,
    bool AlwaysOnTop = false,
    ThemeMode? Theme = null)
{
    public static UiSettings Default { get; } = new();
    public ThemeMode EffectiveTheme => Theme ?? ThemeMode.Dark;
}

public sealed record ProcessingOptions(
    ModelKind Model = ModelKind.FastAndAccurate,
    RefinementSettings? Refinement = null)
{
    public RefinementSettings EffectiveRefinement => Refinement ?? RefinementSettings.Default;
}

public sealed record ImageInput(string Path)
{
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff" };

    public bool IsSupportedLocalFile =>
        !string.IsNullOrWhiteSpace(Path) &&
        System.IO.Path.IsPathFullyQualified(Path) &&
        SupportedExtensions.Contains(System.IO.Path.GetExtension(Path));
}

public sealed record ProcessedImage
{
    public ProcessedImage(byte[] pixels, int width, int height)
    {
        Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA pixels must contain exactly width * height * 4 bytes.", nameof(pixels));
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
}
