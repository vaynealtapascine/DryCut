using System.Runtime.CompilerServices;
using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

[assembly: InternalsVisibleTo("BackgroundCut.Infrastructure.Tests")]

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
            var (session, usingDirectMlForRun) = GetSession(model.Id);
            var io = ResolveModelIo(session, profile);
            var tensor = CreateInputTensor(source, profile, io);
            var reportUsingDirectMl = usingDirectMlForRun;

            RunResult runResult;
            try
            {
                runResult = await Task.Run(() => Run(session, tensor, io), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (usingDirectMlForRun && ex is not OperationCanceledException)
            {
                InferenceSession fallbackSession;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _session?.Dispose();
                    _session = CreateSession(model.Id, useDirectMl: false);
                    _usingDirectMl = false;
                    fallbackSession = _session;
                }
                reportUsingDirectMl = false;
                runResult = await Task.Run(() => Run(fallbackSession, tensor, io), cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ProcessingProgress(
                reportUsingDirectMl ? "Refining edges (GPU)…" : "Refining edges (CPU)…",
                0.80));
            cancellationToken.ThrowIfCancellationRequested();
            var alpha = ResizeMask(
                NormalizeMask(runResult.Values, profile.OutputIsLogits),
                runResult.SourceWidth,
                runResult.SourceHeight,
                source.Width,
                source.Height);
            EdgeRefinement.Apply(alpha, source.Width, source.Height, refinement, source.Pixels);
            progress?.Report(new ProcessingProgress("Finishing transparent image…", 1));
            return ImageSharpImageService.ComposeRgba(source, alpha);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private (InferenceSession Session, bool UsingDirectMl) GetSession(string modelPath)
    {
        lock (_gate)
        {
            if (_session is not null && string.Equals(_loadedModel, modelPath, StringComparison.OrdinalIgnoreCase))
                return (_session, _usingDirectMl);

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
            return (_session, _usingDirectMl);
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

    private readonly record struct RunResult(float[] Values, int SourceWidth, int SourceHeight);

    /// <summary>
    /// Tensor I/O resolved from the session's own metadata rather than hardcoded per model kind.
    /// <see cref="ModelProfile"/>'s expected names are used only as a disambiguation preference.
    /// </summary>
    internal readonly record struct ModelIo(string InputName, bool InputIsFloat16, string OutputName, bool OutputIsFloat16);

    /// <summary>
    /// The subset of <see cref="NodeMetadata"/> the resolution logic needs. <see cref="NodeMetadata"/>
    /// itself has no public constructor, so this narrow shape is what lets
    /// <see cref="ResolveInputName"/>/<see cref="ResolveOutputName"/> be unit tested without a real
    /// InferenceSession/model file.
    /// </summary>
    internal readonly record struct TensorInfo(string Name, int[] Dimensions, Type ElementType);

    /// <summary>
    /// Inspects <paramref name="session"/>'s declared inputs/outputs and picks the tensor names and
    /// element types actually used by the loaded graph. This is what lets the same code path handle
    /// both the float32 IS-Net graph (names "input"/"output") and a float16 BiRefNet export whose
    /// names or dtype might differ from the historical assumption.
    /// </summary>
    internal static ModelIo ResolveModelIo(InferenceSession session, ModelProfile profile)
    {
        var inputs = session.InputMetadata.Select(kv => new TensorInfo(kv.Key, kv.Value.Dimensions, kv.Value.ElementType)).ToArray();
        var outputs = session.OutputMetadata.Select(kv => new TensorInfo(kv.Key, kv.Value.Dimensions, kv.Value.ElementType)).ToArray();
        var inputName = ResolveInputName(inputs, profile.InputName);
        var outputName = ResolveOutputName(outputs, profile.OutputName);
        var inputIsFloat16 = IsFloat16(session.InputMetadata[inputName].ElementType);
        var outputIsFloat16 = IsFloat16(session.OutputMetadata[outputName].ElementType);
        return new ModelIo(inputName, inputIsFloat16, outputName, outputIsFloat16);
    }

    private static bool IsFloat16(Type elementType) => elementType == typeof(Float16);

    internal static string ResolveInputName(IReadOnlyCollection<TensorInfo> inputs, string preferredName)
    {
        if (inputs.Count == 0)
            throw new InvalidOperationException("The ONNX model declares no inputs.");
        if (inputs.Count == 1)
            return inputs.Single().Name;
        var preferred = inputs.FirstOrDefault(i => i.Name == preferredName);
        if (preferred.Name is not null)
            return preferred.Name;

        // Multiple inputs and none match the profile's preferred name: fall back to the first 4-D
        // float/float16 input, which is the shape a single-image segmentation graph's pixel input takes.
        foreach (var input in inputs)
        {
            if (input.Dimensions.Length == 4 && (input.ElementType == typeof(float) || IsFloat16(input.ElementType)))
                return input.Name;
        }

        throw new InvalidOperationException(
            $"Could not determine the image input tensor: the model has {inputs.Count} inputs, none named " +
            $"'{preferredName}' or matching a 4-D float/float16 image tensor. Available inputs: " +
            string.Join(", ", inputs.Select(i => i.Name)));
    }

    internal static string ResolveOutputName(IReadOnlyCollection<TensorInfo> outputs, string preferredName)
    {
        if (outputs.Count == 0)
            throw new InvalidOperationException("The ONNX model declares no outputs.");
        if (outputs.Count == 1)
            return outputs.Single().Name;
        var preferred = outputs.FirstOrDefault(o => o.Name == preferredName);
        if (preferred.Name is not null)
            return preferred.Name;

        // BiRefNet-style exports can carry several outputs (multi-scale supervision heads in addition
        // to the final mask). Prefer the profile's expected name; otherwise pick the first output whose
        // shape is 4-D NCHW with a single channel (dims[1] == 1), which is what a mask head looks like
        // (batch dim[0] is also typically 1, so it alone isn't a reliable signal).
        foreach (var output in outputs)
        {
            if (output.Dimensions.Length == 4 && output.Dimensions[1] == 1)
                return output.Name;
        }

        throw new InvalidOperationException(
            $"Could not determine the mask output tensor: the model has {outputs.Count} outputs, none named " +
            $"'{preferredName}' or matching a 4-D single-channel mask tensor. Available outputs: " +
            string.Join(", ", outputs.Select(o => o.Name)));
    }

    private static RunResult Run(InferenceSession session, NamedOnnxValue input, ModelIo io)
    {
        using var results = session.Run(new[] { input }, new[] { io.OutputName });
        var output = results.First(value => value.Name == io.OutputName);
        if (io.OutputIsFloat16)
        {
            var float16Tensor = output.AsTensor<Float16>();
            var dims = float16Tensor.Dimensions;
            var width = dims[^1];
            var height = dims[^2];
            var flat = float16Tensor.ToArray();
            var converted = new float[flat.Length];
            for (var i = 0; i < flat.Length; i++)
                converted[i] = (float)flat[i];
            return new RunResult(converted, width, height);
        }
        else
        {
            var tensor = output.AsTensor<float>();
            var dims = tensor.Dimensions;
            return new RunResult(tensor.ToArray(), dims[^1], dims[^2]);
        }
    }

    private static NamedOnnxValue CreateInputTensor(DecodedImage source, ModelProfile profile, ModelIo io)
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

        var dims = new[] { 1, 3, InputSize, InputSize };
        if (io.InputIsFloat16)
        {
            // Normalization above stays in float for unchanged precision behaviour; only the storage
            // type used to hand the tensor to the runtime changes here.
            var half = new Float16[values.Length];
            for (var i = 0; i < values.Length; i++)
                half[i] = (Float16)values[i];
            return NamedOnnxValue.CreateFromTensor(io.InputName, new DenseTensor<Float16>(half, dims));
        }

        return NamedOnnxValue.CreateFromTensor(io.InputName, new DenseTensor<float>(values, dims));
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

    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(5);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Mark disposed under the gate first so any in-flight ProcessAsync (already past its own
            // ObjectDisposedException check) and any subsequent call both observe the disposed state,
            // instead of blocking shutdown indefinitely on the process gate below.
            _disposed = true;
        }

        // Give an in-flight inference a bounded window to finish before tearing down native resources,
        // rather than blocking the UI thread until it completes.
        if (!_processGate.Wait(DisposeWaitTimeout))
        {
            // The inference is still running inside native ONNX Runtime code. Disposing the session
            // out from under it risks an access violation, and disposing the gate would make that
            // call's own Release() throw from a finally block. The process is shutting down, so
            // leaving both to be reclaimed at exit is strictly safer than tearing them down here.
            return;
        }

        try
        {
            lock (_gate)
            {
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

    internal sealed record ModelProfile(ModelKind Kind, string InputName, string OutputName, bool OutputIsLogits)
    {
        public static ModelProfile For(ModelKind kind) => kind switch
        {
            ModelKind.FastAndAccurate => new(kind, "input", "output", false),
            ModelKind.HighestQuality => new(kind, "input_image", "output_image", true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported model profile.")
        };
    }
}
