using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BackgroundCut.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void ComposeRgbaPreservesColorAndUsesMaskAsAlpha()
    {
        var source = new DecodedImage(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, 2, 1);
        var result = ImageSharpImageService.ComposeRgba(source, new[] { 0f, 0.5f });
        Assert.Equal(new byte[] { 10, 20, 30, 0, 40, 50, 60, 128 }, result.Pixels);
    }

    [Fact]
    public void EdgeRefinementOnlyChangesUncertainBand()
    {
        var source = Enumerable.Repeat((byte)100, 3 * 3 * 4).ToArray();
        var mask = new[] { 0f, 0f, 0f, 0f, .5f, 1f, 1f, 1f, 1f };
        EdgeRefinement.Apply(mask, 3, 3, new RefinementSettings(RefinementPreset.Balanced), source);
        Assert.Equal(0f, mask[0]);
        Assert.Equal(1f, mask[8]);
        Assert.InRange(mask[4], 0.45f, 0.55f);
    }

    [Fact]
    public async Task JsonSettingsRoundTripsAtomically()
    {
        var path = Path.Combine(Path.GetTempPath(), "backgroundcut-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonSettingsStore(path);
        var expected = new ExportSettings(ExportPolicy.SourceFolder, "C:/Pictures");
        await store.SaveExportSettingsAsync(expected, CancellationToken.None);
        Assert.Equal(expected, await store.LoadExportSettingsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PngExportAddsCollisionSuffixAndKeepsAlpha()
    {
        var folder = Path.Combine(Path.GetTempPath(), "backgroundcut-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "image-background-removed.png"), new byte[] { 1 });
        var image = new ProcessedImage(new byte[] { 1, 2, 3, 0 }, 1, 1);
        var result = await new PngExportService().ExportAsync(image, new ExportRequest(ExportPolicy.DefaultFolder, folder, null), CancellationToken.None);
        Assert.EndsWith("image-background-removed (2).png", result.Path);
        using var decoded = await Image.LoadAsync<Rgba32>(result.Path);
        Assert.Equal((byte)0, decoded[0, 0].A);
    }

    [Fact]
    public async Task RealModelSmokeIsOptIn()
    {
        var model = Environment.GetEnvironmentVariable("BACKGROUNDCUT_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(model)) return;
        var imagePath = Path.Combine(Path.GetTempPath(), "backgroundcut-smoke.png");
        using (var image = new Image<Rgba32>(32, 32, new Rgba32(255, 0, 0, 255))) await image.SaveAsPngAsync(imagePath);
        using var engine = new OnnxBackgroundRemovalEngine();
        var output = await engine.ProcessAsync(new ImageInput(imagePath), new ModelDescriptor(ModelKind.FastAndAccurate, model, "smoke", true, true), RefinementSettings.Default, null, CancellationToken.None);
        Assert.Equal(32, output.Width);
        Assert.Equal(32, output.Height);
        Assert.Contains(output.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha is > 0 and < 255);
    }
}
