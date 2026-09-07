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
    private InferenceSession? _session;
    private string? _loadedModel;
    private bool _usingDirectMl;

    public OnnxBackgroundRemovalEngine() { }

    public bool UsingDirectMl => _usingDirectMl;

    public async Task<ProcessedImage> ProcessAsync(ImageInput input, ModelDescriptor model, RefinementSettings refinement, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(refinement);
        if (!File.Exists(model.Id))
            throw new FileNotFoundException("The selected ONNX model was not found.", model.Id);

        var source = await ImageSharpImageService.DecodeAsync(input.Path, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProcessingProgress("Decoded image", 0.15));
        cancellationToken.ThrowIfCancellationRequested();
        var tensor = CreateInputTensor(source);
        var session = GetSession(model.Id);
        float[] values;
        try
        {
            values = Run(session, tensor);
        }
        catch (Exception ex) when (_usingDirectMl && ex is not OperationCanceledException)
        {
            lock (_gate)
            {
                _session?.Dispose();
                _session = CreateSession(model.Id, useDirectMl: false);
                _usingDirectMl = false;
            }
            values = Run(_session!, tensor);
        }
        progress?.Report(new ProcessingProgress(_usingDirectMl ? "Processed with DirectML" : "Processed with CPU", 0.75));
        cancellationToken.ThrowIfCancellationRequested();
        var alpha = ResizeMask(NormalizeMask(values), InputSize, InputSize, source.Width, source.Height);
        EdgeRefinement.Apply(alpha, source.Width, source.Height, refinement, source.Pixels);
        progress?.Report(new ProcessingProgress("Composited result", 1));
        return ImageSharpImageService.ComposeRgba(source, alpha);
    }

    private InferenceSession GetSession(string modelPath)
    {
        lock (_gate)
        {
            if (_session is not null && string.Equals(_loadedModel, modelPath, StringComparison.OrdinalIgnoreCase)) return _session;
            _session?.Dispose();
            try
            {
                _session = CreateSession(modelPath, useDirectMl: true);
                _usingDirectMl = true;
            }
            catch (Exception) when (OperatingSystem.IsWindows())
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
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (useDirectMl) AppendDirectMl(options);
        return new InferenceSession(modelPath, options);
    }

    private static void AppendDirectMl(SessionOptions options)
    {
        var method = typeof(SessionOptions).GetMethod("AppendExecutionProvider_DML", new[] { typeof(int) })
            ?? typeof(SessionOptions).GetMethod("AppendExecutionProvider_Dml", new[] { typeof(int) });
        if (method is not null)
        {
            method.Invoke(options, new object[] { 0 });
            return;
        }
        var generic = typeof(SessionOptions).GetMethod("AppendExecutionProvider", new[] { typeof(string), typeof(Dictionary<string, string>) });
        if (generic is not null)
        {
            generic.Invoke(options, new object[] { "DML", new Dictionary<string, string> { ["device_id"] = "0" } });
            return;
        }
        throw new PlatformNotSupportedException("This ONNX Runtime build does not expose the DirectML execution provider.");
    }

    private static float[] Run(InferenceSession session, DenseTensor<float> input)
    {
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("input", input) });
        var tensor = results[0].AsTensor<float>();
        return tensor.ToArray();
    }

    private static DenseTensor<float> CreateInputTensor(DecodedImage source)
    {
        using var image = Image.LoadPixelData<Rgba32>(source.Pixels, source.Width, source.Height);
        image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(InputSize, InputSize), Mode = ResizeMode.Stretch }));
        var values = new float[3 * InputSize * InputSize];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < InputSize; y++)
            for (var x = 0; x < InputSize; x++)
            {
                var pixel = accessor.GetRowSpan(y)[x];
                var index = y * InputSize + x;
                values[index] = (pixel.R - 128f) / 256f;
                values[InputSize * InputSize + index] = (pixel.G - 128f) / 256f;
                values[2 * InputSize * InputSize + index] = (pixel.B - 128f) / 256f;
            }
        });
        return new DenseTensor<float>(values, new[] { 1, 3, InputSize, InputSize });
    }

    private static float[] NormalizeMask(float[] raw)
    {
        var min = raw.Min();
        var max = raw.Max();
        if (min >= 0 && max <= 1) return raw.Select(value => Math.Clamp(value, 0f, 1f)).ToArray();
        if (min < 0 && max > 0) return raw.Select(value => 1f / (1f + MathF.Exp(-Math.Clamp(value, -20f, 20f)))).ToArray();
        var range = MathF.Max(max - min, 0.000001f);
        return raw.Select(value => Math.Clamp((value - min) / range, 0f, 1f)).ToArray();
    }

    private static float[] ResizeMask(float[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var result = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            var sy = (y + 0.5f) * sourceHeight / height - 0.5f;
            var y0 = Math.Clamp((int)MathF.Floor(sy), 0, sourceHeight - 1);
            var y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
            var fy = Math.Clamp(sy - MathF.Floor(sy), 0, 1);
            for (var x = 0; x < width; x++)
            {
                var sx = (x + 0.5f) * sourceWidth / width - 0.5f;
                var x0 = Math.Clamp((int)MathF.Floor(sx), 0, sourceWidth - 1);
                var x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
                var fx = Math.Clamp(sx - MathF.Floor(sx), 0, 1);
                var top = source[y0 * sourceWidth + x0] * (1 - fx) + source[y0 * sourceWidth + x1] * fx;
                var bottom = source[y1 * sourceWidth + x0] * (1 - fx) + source[y1 * sourceWidth + x1] * fx;
                result[y * width + x] = top * (1 - fy) + bottom * fy;
            }
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate) _session?.Dispose();
        _session = null;
    }
}
