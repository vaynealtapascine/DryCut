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
    public async Task JsonUiSettingsRoundTripAtomicallyAndIndependentlyOfExportSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "backgroundcut-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonSettingsStore(path);
        var expected = new UiSettings(ViewMode.Panel, 512, AlwaysOnTop: true);

        await store.SaveUiSettingsAsync(expected, CancellationToken.None);

        Assert.Equal(expected, await store.LoadUiSettingsAsync(CancellationToken.None));
        // Saving UI settings must not disturb export settings, which live in a sibling file.
        Assert.Equal(ExportSettings.Default, await store.LoadExportSettingsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task JsonUiSettingsReturnsDefaultsWhenMissingOrCorrupt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "backgroundcut-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var store = new JsonSettingsStore(path);

        Assert.Equal(UiSettings.Default, await store.LoadUiSettingsAsync(CancellationToken.None));

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "ui-settings.json"), "{ not valid json");

        Assert.Equal(UiSettings.Default, await store.LoadUiSettingsAsync(CancellationToken.None));
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
    public async Task ProcessAfterDisposeFailsBeforeOpeningNativeResources()
    {
        var placeholderModel = Path.GetTempFileName();
        var engine = new OnnxBackgroundRemovalEngine();
        try
        {
            engine.Dispose();
            engine.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ProcessAsync(
                new ImageInput("C:/missing.png"),
                new ModelDescriptor(ModelKind.FastAndAccurate, placeholderModel, "placeholder", true, true),
                RefinementSettings.Default,
                null,
                CancellationToken.None));
        }
        finally
        {
            engine.Dispose();
            File.Delete(placeholderModel);
        }
    }

    [Fact]
    public async Task DownloadRestartsInsteadOfLoopingOn416WhenPartialAlreadyMatchesTargetLength()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BackgroundCut-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var content = new byte[] { 1, 2, 3, 4 };
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
            var artifact = new ModelArtifact(
                ModelKind.HighestQuality,
                "resume-416.onnx",
                new Uri("https://example.invalid/model.onnx"),
                content.Length,
                sha,
                "MIT");
            // A stale .partial whose length already equals the target length slips past the
            // `existing > artifact.Length` guard, so a Range request goes out and HuggingFace-style
            // servers answer 416 for it. This used to loop forever; it must now restart from scratch.
            await File.WriteAllBytesAsync(Path.Combine(directory, "resume-416.onnx.partial"), new byte[] { 9, 9, 9, 9 });
            using var handler = new RangeAwareHandler(content, System.Net.HttpStatusCode.RequestedRangeNotSatisfiable);
            using var client = new HttpClient(handler);
            var downloader = new ModelDownloader(client);

            var path = await downloader.DownloadAsync(artifact, directory);

            Assert.True(File.Exists(path));
            Assert.Equal(content, await File.ReadAllBytesAsync(path));
            Assert.True(handler.SawRangeRequest);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadRestartsWhenServerIgnoresRequestedResumeOffset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BackgroundCut-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var content = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
            var artifact = new ModelArtifact(
                ModelKind.HighestQuality,
                "resume-rebase.onnx",
                new Uri("https://example.invalid/model.onnx"),
                content.Length,
                sha,
                "MIT");
            // A partial with 8 bytes already downloaded; the server will claim 206 but re-base to 0
            // instead of honoring the requested offset. The client must detect the mismatch and restart.
            await File.WriteAllBytesAsync(Path.Combine(directory, "resume-rebase.onnx.partial"), content.Take(8).ToArray());
            using var handler = new MismatchedContentRangeHandler(content);
            using var client = new HttpClient(handler);
            var downloader = new ModelDownloader(client);

            var path = await downloader.DownloadAsync(artifact, directory);

            Assert.True(File.Exists(path));
            Assert.Equal(content, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PngExportAskEveryTimeOverwritesTheExactRequestedPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "backgroundcut-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var requestedPath = Path.Combine(folder, "chosen.png");
        // Simulate the user picking an existing file in the "Save as..." dialog and confirming the
        // dialog's own overwrite prompt.
        await File.WriteAllBytesAsync(requestedPath, new byte[] { 9 });
        var image = new ProcessedImage(new byte[] { 1, 2, 3, 0 }, 1, 1);
        var request = new ExportRequest(ExportPolicy.AskEveryTime, null, null, RequestedPath: requestedPath);

        var result = await new PngExportService().ExportAsync(image, request, CancellationToken.None);

        Assert.Equal(requestedPath, result.Path);
        Assert.False(File.Exists(Path.Combine(folder, "chosen (2).png")));
        using var decoded = await Image.LoadAsync<Rgba32>(result.Path);
        Assert.Equal((byte)0, decoded[0, 0].A);
    }

    private static readonly int[] Dims1x1x1 = { 1 };
    private static readonly int[] Dims1x3x1024x1024 = { 1, 3, 1024, 1024 };
    private static readonly int[] Dims1x1x1024x1024 = { 1, 1, 1024, 1024 };
    private static readonly int[] Dims1x1x512x512 = { 1, 1, 512, 512 };
    private static readonly int[] Dims1x64x128x128 = { 1, 64, 128, 128 };
    private static readonly int[] Dims2x3 = { 2, 3 };

    [Fact]
    public void ResolveInputNameReturnsTheSoleInputRegardlessOfName()
    {
        var inputs = new[] { new OnnxBackgroundRemovalEngine.TensorInfo("pixel_values", Dims1x3x1024x1024, typeof(float)) };
        Assert.Equal("pixel_values", OnnxBackgroundRemovalEngine.ResolveInputName(inputs, "input"));
    }

    [Fact]
    public void ResolveInputNamePrefersTheProfileNameWhenSeveralInputsExist()
    {
        var inputs = new[]
        {
            new OnnxBackgroundRemovalEngine.TensorInfo("some_other_input", Dims1x3x1024x1024, typeof(float)),
            new OnnxBackgroundRemovalEngine.TensorInfo("input_image", Dims1x3x1024x1024, typeof(float)),
        };
        Assert.Equal("input_image", OnnxBackgroundRemovalEngine.ResolveInputName(inputs, "input_image"));
    }

    [Fact]
    public void ResolveInputNameFallsBackToTheFirstFourDimensionalFloatInputWhenNoneMatchesThePreferredName()
    {
        var inputs = new[]
        {
            new OnnxBackgroundRemovalEngine.TensorInfo("scale_factor", Dims1x1x1, typeof(float)),
            new OnnxBackgroundRemovalEngine.TensorInfo("pixel_values", Dims1x3x1024x1024, typeof(float)),
        };
        Assert.Equal("pixel_values", OnnxBackgroundRemovalEngine.ResolveInputName(inputs, "input"));
    }

    [Fact]
    public void ResolveInputNameAcceptsAFloat16FourDimensionalInputAsFallback()
    {
        var inputs = new[]
        {
            new OnnxBackgroundRemovalEngine.TensorInfo("scale_factor", Dims1x1x1, typeof(float)),
            new OnnxBackgroundRemovalEngine.TensorInfo("pixel_values", Dims1x3x1024x1024, typeof(Microsoft.ML.OnnxRuntime.Float16)),
        };
        Assert.Equal("pixel_values", OnnxBackgroundRemovalEngine.ResolveInputName(inputs, "input"));
    }

    [Fact]
    public void ResolveInputNameThrowsADiagnosableExceptionWhenNothingMatches()
    {
        var inputs = new[]
        {
            new OnnxBackgroundRemovalEngine.TensorInfo("a", Dims1x1x1, typeof(float)),
            new OnnxBackgroundRemovalEngine.TensorInfo("b", Dims1x1x1, typeof(int)),
        };
        var ex = Assert.Throws<InvalidOperationException>(() => OnnxBackgroundRemovalEngine.ResolveInputName(inputs, "input"));
        Assert.Contains("image input tensor", ex.Message);
    }

    [Fact]
    public void ResolveInputNameThrowsWhenThereAreNoInputs()
    {
        Assert.Throws<InvalidOperationException>(() => OnnxBackgroundRemovalEngine.ResolveInputName(Array.Empty<OnnxBackgroundRemovalEngine.TensorInfo>(), "input"));
    }

    [Fact]
    public void ResolveOutputNameReturnsTheSoleOutputRegardlessOfName()
    {
        var outputs = new[] { new OnnxBackgroundRemovalEngine.TensorInfo("logits", Dims1x1x1024x1024, typeof(float)) };
        Assert.Equal("logits", OnnxBackgroundRemovalEngine.ResolveOutputName(outputs, "output"));
    }

    [Fact]
    public void ResolveOutputNamePrefersTheProfileNameWhenSeveralOutputsExist()
    {
        var outputs = new[]
        {
            new OnnxBackgroundRemovalEngine.TensorInfo("aux_supervision_head", Dims1x1x512x512, typeof(float)),
            new OnnxBackgroundRemovalEngine.TensorInfo("output_image", Dims1x1x1024x1024, typeof(float)),
        };
        Assert.Equal("output_image", OnnxBackgroundRemovalEngine.ResolveOutputName(outputs, "output_image"));
    }

    [Fact]
    public void ResolveOutputNameFallsBackToTheFirstFourDimensionalSingleChannelMaskWhenNoneMatchesThePreferredName()
    {
        var outputs = new[]
        {
            new OnnxBackgroundRemovalEngine.TensorInfo("feature_map", Dims1x64x128x128, typeof(float)),
            new OnnxBackgroundRemovalEngine.TensorInfo("mask_head", Dims1x1x1024x1024, typeof(float)),
        };
        Assert.Equal("mask_head", OnnxBackgroundRemovalEngine.ResolveOutputName(outputs, "output"));
    }

    [Fact]
    public void ResolveOutputNameThrowsADiagnosableExceptionWhenNothingMatches()
    {
        var outputs = new[]
        {
            new OnnxBackgroundRemovalEngine.TensorInfo("a", Dims1x64x128x128, typeof(float)),
            new OnnxBackgroundRemovalEngine.TensorInfo("b", Dims2x3, typeof(float)),
        };
        var ex = Assert.Throws<InvalidOperationException>(() => OnnxBackgroundRemovalEngine.ResolveOutputName(outputs, "output"));
        Assert.Contains("mask output tensor", ex.Message);
    }

    [Fact]
    public void ResolveOutputNameThrowsWhenThereAreNoOutputs()
    {
        Assert.Throws<InvalidOperationException>(() => OnnxBackgroundRemovalEngine.ResolveOutputName(Array.Empty<OnnxBackgroundRemovalEngine.TensorInfo>(), "output"));
    }

    [Fact]
    public void IsNetProfileStillUsesItsHistoricalInputAndOutputNames()
    {
        var profile = OnnxBackgroundRemovalEngine.ModelProfile.For(ModelKind.FastAndAccurate);
        Assert.Equal("input", profile.InputName);
        Assert.Equal("output", profile.OutputName);
        Assert.False(profile.OutputIsLogits);
    }

    [Fact]
    public void BiRefNetProfileStillUsesItsHistoricalInputAndOutputNamesAndLogitsOutput()
    {
        var profile = OnnxBackgroundRemovalEngine.ModelProfile.For(ModelKind.HighestQuality);
        Assert.Equal("input_image", profile.InputName);
        Assert.Equal("output_image", profile.OutputName);
        Assert.True(profile.OutputIsLogits);
    }

    [Fact]
    public void DisposeDuringInFlightProcessingDoesNotHangIndefinitely()
    {
        var engine = new OnnxBackgroundRemovalEngine();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        engine.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "Dispose must not block indefinitely.");
        engine.Dispose();
    }

    [Fact]
    public async Task RealModelSmokeSkipsUnlessBackgroundcutTestModelIsSet()
    {
        var model = Environment.GetEnvironmentVariable("BACKGROUNDCUT_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(model))
        {
            Console.WriteLine("SKIPPED — env var BACKGROUNDCUT_TEST_MODEL not set; the real-model smoke test did not run.");
            return;
        }
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
    public async Task StrongModelSmokeSkipsUnlessBackgroundcutTestStrongModelIsSet()
    {
        var model = Environment.GetEnvironmentVariable("BACKGROUNDCUT_TEST_STRONG_MODEL");
        if (string.IsNullOrWhiteSpace(model))
        {
            Console.WriteLine("SKIPPED — env var BACKGROUNDCUT_TEST_STRONG_MODEL not set; the strong-model smoke test did not run.");
            return;
        }
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

    /// <summary>Answers ranged requests with a fixed status (e.g. 416) and unranged requests with the full body.</summary>
    private sealed class RangeAwareHandler(byte[] content, System.Net.HttpStatusCode rangeResponseStatus) : HttpMessageHandler
    {
        public bool SawRangeRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Range is not null)
            {
                SawRangeRequest = true;
                return Task.FromResult(new HttpResponseMessage(rangeResponseStatus));
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }

    /// <summary>Simulates a server that answers 206 to a ranged request but re-bases the range to 0 instead of honoring the requested offset.</summary>
    private sealed class MismatchedContentRangeHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Range is not null)
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(content)
                };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, content.Length - 1, content.Length);
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }
}
