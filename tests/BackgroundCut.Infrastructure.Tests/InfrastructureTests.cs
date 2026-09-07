using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BackgroundCut.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void ModelArtifactsArePinnedAndDownloadable()
    {
        foreach (var artifact in ModelCatalog.All)
        {
            Assert.Equal(Uri.UriSchemeHttps, artifact.DownloadUri.Scheme);
            Assert.True(artifact.Length > 0);
            Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
        }
    }

    [Fact]
    public async Task InvalidModelDownloadIsNotLeftBehind()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BackgroundCut-tests", Guid.NewGuid().ToString("N"));
        var artifact = new ModelArtifact(
            ModelKind.HighestQuality,
            "invalid.onnx",
            new Uri("https://example.invalid/model.onnx"),
            4,
            new string('0', 64),
            "MIT");
        using var client = new HttpClient(new StaticResponseHandler([1, 2, 3, 4]));
        var downloader = new ModelDownloader(client);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(artifact, directory));
            Assert.False(File.Exists(Path.Combine(directory, "invalid.onnx")));
            Assert.False(File.Exists(Path.Combine(directory, "invalid.onnx.partial")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

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
    public async Task PngExportUsesSuggestedSourceName()
    {
        var folder = Path.Combine(Path.GetTempPath(), "backgroundcut-tests", Guid.NewGuid().ToString("N"));
        var image = new ProcessedImage(new byte[] { 1, 2, 3, 255 }, 1, 1);
        var request = new ExportRequest(ExportPolicy.DefaultFolder, folder, null, SuggestedFileName: "portrait-background-removed.png");
        var result = await new PngExportService().ExportAsync(image, request, CancellationToken.None);
        Assert.Equal("portrait-background-removed.png", Path.GetFileName(result.Path));
    }

    [Fact]
    public async Task RealModelSmokeIsOptIn()
    {
        var model = Environment.GetEnvironmentVariable("BACKGROUNDCUT_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(model)) return;
        var imagePath = Path.Combine(Path.GetTempPath(), "backgroundcut-smoke.png");
        using (var image = new Image<Rgba32>(512, 512, new Rgba32(255, 255, 255, 255)))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 70; y < 426; y++)
                    for (var x = 105; x < 408; x++)
                    {
                        var dx = (x - 256) / 151f;
                        var dy = (y - 247) / 178f;
                        if (dx * dx + dy * dy <= 1) accessor.GetRowSpan(y)[x] = new Rgba32(25, 95, 210, 255);
                    }
            });
            await image.SaveAsPngAsync(imagePath);
        }
        using var engine = new OnnxBackgroundRemovalEngine();
        var output = await engine.ProcessAsync(new ImageInput(imagePath), new ModelDescriptor(ModelKind.FastAndAccurate, model, "smoke", true, true), RefinementSettings.Default, null, CancellationToken.None);
        Console.WriteLine($"Fast model DirectML active: {engine.UsingDirectMl}");
        Assert.Equal(512, output.Width);
        Assert.Equal(512, output.Height);
        var alpha = output.Pixels.Where((_, index) => index % 4 == 3).ToArray();
        Assert.True(alpha.Min() < 16, "Expected a substantially transparent background.");
        Assert.True(alpha.Max() > 239, "Expected a substantially opaque foreground.");
    }

    [Fact]
    public async Task StrongModelSmokeIsOptIn()
    {
        var model = Environment.GetEnvironmentVariable("BACKGROUNDCUT_TEST_STRONG_MODEL");
        if (string.IsNullOrWhiteSpace(model)) return;
        var imagePath = Path.Combine(Path.GetTempPath(), "backgroundcut-strong-smoke.png");
        using (var image = new Image<Rgba32>(512, 512, new Rgba32(245, 245, 245, 255)))
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 72; y < 440; y++)
                    for (var x = 110; x < 402; x++)
                    {
                        var dx = (x - 256) / 146f;
                        var dy = (y - 255) / 184f;
                        if (dx * dx + dy * dy <= 1) accessor.GetRowSpan(y)[x] = new Rgba32(30, 110, 210, 255);
                    }
            });
            await image.SaveAsPngAsync(imagePath);
        }
        using var engine = new OnnxBackgroundRemovalEngine();
        var output = await engine.ProcessAsync(new ImageInput(imagePath), new ModelDescriptor(ModelKind.HighestQuality, model, "strong smoke", false, true), new RefinementSettings(RefinementPreset.None), null, CancellationToken.None);
        Console.WriteLine($"Strong model DirectML active: {engine.UsingDirectMl}");
        var alpha = output.Pixels.Where((_, index) => index % 4 == 3).ToArray();
        Assert.True(alpha.Min() < 16, "Expected a substantially transparent background.");
        Assert.True(alpha.Max() > 239, "Expected a substantially opaque foreground.");
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
    }
}
