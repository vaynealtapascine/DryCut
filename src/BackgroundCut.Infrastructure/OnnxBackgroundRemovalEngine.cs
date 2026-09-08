using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BackgroundCut.Infrastructure;

public sealed class OnnxBackgroundRemovalEngine : IBackgroundRemovalEngine, IDisposable
{
    private const int InputSize = 1024;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private InferenceSession? _session;
    private string? _loadedModel;
    private bool _usingDirectMl;
    private bool _disposed;

    public bool UsingDirectMl => _usingDirectMl;

    public async Task<ProcessedImage> ProcessAsync(
        ImageInput input,
        ModelDescriptor model,
        RefinementSettings refinement,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(refinement);

        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
                ObjectDisposedException.ThrowIf(_disposed, this);

            if (!File.Exists(model.Id))
                throw new FileNotFoundException("The selected ONNX model was not found.", model.Id);

            progress?.Report(new ProcessingProgress("Opening image…", 0.05));
            var source = await ImageSharpImageService.DecodeAsync(input.Path, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ProcessingProgress("Preparing image…", 0.18));
            cancellationToken.ThrowIfCancellationRequested();

            var profile = ModelProfile.For(model.Kind);
            var tensor = CreateInputTensor(source, profile);
            var session = GetSession(model.Id);

            float[] values;
            try
            {
                values = await Task.Run(() => Run(session, tensor, profile), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_usingDirectMl && ex is not OperationCanceledException)
            {
                lock (_gate)
                {
                    _session?.Dispose();
                    _session = CreateSession(model.Id, useDirectMl: false);
                    _usingDirectMl = false;
                }
                values = await Task.Run(() => Run(_session!, tensor, profile), cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ProcessingProgress(
                _usingDirectMl ? "Refining edges (GPU)…" : "Refining edges (CPU)…",
                0.80));
            cancellationToken.ThrowIfCancellationRequested();
            var alpha = ResizeMask(NormalizeMask(values, profile.OutputIsLogits), InputSize, InputSize, source.Width, source.Height);
            EdgeRefinement.Apply(alpha, source.Width, source.Height, refinement, source.Pixels);
            progress?.Report(new ProcessingProgress("Finishing transparent image…", 1));
            return ImageSharpImageService.ComposeRgba(source, alpha);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private InferenceSession GetSession(string modelPath)
    {
        lock (_gate)
        {
            if (_session is not null && string.Equals(_loadedModel, modelPath, StringComparison.OrdinalIgnoreCase))
                return _session;

            _session?.Dispose();
            try
            {
                _session = CreateSession(modelPath, useDirectMl: true);
                _usingDirectMl = true;
            }
            catch when (OperatingSystem.IsWindows())
            {
                _session = CreateSession(modelPath, useDirectMl: false);
                _usingDirectMl = false;
            }
            _loadedModel = modelPath;
            return _session;
        }
    }

    private static InferenceSession CreateSession(string modelPath, bool useDirectMl)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        if (useDirectMl)
        {
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
        }
        return new InferenceSession(modelPath, options);
    }

    private static float[] Run(InferenceSession session, DenseTensor<float> input, ModelProfile profile)
    {
        var inputValue = NamedOnnxValue.CreateFromTensor(profile.InputName, input);
        using var results = session.Run(new[] { inputValue }, new[] { profile.OutputName });
        return results[0].AsTensor<float>().ToArray();
    }

    private static DenseTensor<float> CreateInputTensor(DecodedImage source, ModelProfile profile)
    {
        using var image = Image.LoadPixelData<Rgba32>(source.Pixels, source.Width, source.Height);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(InputSize, InputSize),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic
        }));

        var plane = InputSize * InputSize;
        var values = new float[3 * plane];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < InputSize; y++)
                for (var x = 0; x < InputSize; x++)
                {
                    var pixel = accessor.GetRowSpan(y)[x];
                    var index = y * InputSize + x;
                    if (profile.Kind == ModelKind.FastAndAccurate)
                    {
                        values[index] = (pixel.R - 128f) / 256f;
                        values[plane + index] = (pixel.G - 128f) / 256f;
                        values[2 * plane + index] = (pixel.B - 128f) / 256f;
                    }
                    else
                    {
                        values[index] = (pixel.R / 255f - 0.485f) / 0.229f;
                        values[plane + index] = (pixel.G / 255f - 0.456f) / 0.224f;
                        values[2 * plane + index] = (pixel.B / 255f - 0.406f) / 0.225f;
                    }
                }
        });
        return new DenseTensor<float>(values, new[] { 1, 3, InputSize, InputSize });
    }

    private static float[] NormalizeMask(float[] raw, bool outputIsLogits)
    {
        if (outputIsLogits)
            return raw.Select(value => 1f / (1f + MathF.Exp(-Math.Clamp(value, -20f, 20f)))).ToArray();

        var min = raw.Min();
        var max = raw.Max();
        if (min >= 0 && max <= 1)
            return raw.Select(value => Math.Clamp(value, 0f, 1f)).ToArray();

        var range = MathF.Max(max - min, 0.000001f);
        return raw.Select(value => Math.Clamp((value - min) / range, 0f, 1f)).ToArray();
    }

    private static float[] ResizeMask(float[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        if (source.Length < sourceWidth * sourceHeight)
            throw new InvalidDataException("The model returned a mask with unexpected dimensions.");

        var result = new float[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            var sy = (y + 0.5f) * sourceHeight / height - 0.5f;
            var floorY = MathF.Floor(sy);
            var y0 = Math.Clamp((int)floorY, 0, sourceHeight - 1);
            var y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
            var fy = Math.Clamp(sy - floorY, 0, 1);
            for (var x = 0; x < width; x++)
            {
                var sx = (x + 0.5f) * sourceWidth / width - 0.5f;
                var floorX = MathF.Floor(sx);
                var x0 = Math.Clamp((int)floorX, 0, sourceWidth - 1);
                var x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
                var fx = Math.Clamp(sx - floorX, 0, 1);
                var top = source[y0 * sourceWidth + x0] * (1 - fx) + source[y0 * sourceWidth + x1] * fx;
                var bottom = source[y1 * sourceWidth + x0] * (1 - fx) + source[y1 * sourceWidth + x1] * fx;
                result[y * width + x] = top * (1 - fy) + bottom * fy;
            }
        }
        return result;
    }

    public void Dispose()
    {
        _processGate.Wait();
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _session?.Dispose();
                _session = null;
                _loadedModel = null;
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    private sealed record ModelProfile(ModelKind Kind, string InputName, string OutputName, bool OutputIsLogits)
    {
        public static ModelProfile For(ModelKind kind) => kind switch
        {
            ModelKind.FastAndAccurate => new(kind, "input", "output", false),
            ModelKind.HighestQuality => new(kind, "input_image", "output_image", true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported model profile.")
        };
    }
}
