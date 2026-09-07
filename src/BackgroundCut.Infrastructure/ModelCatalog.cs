using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Infrastructure;

public sealed record ModelArtifact(
    ModelKind Kind,
    string FileName,
    Uri DownloadUri,
    long Length,
    string Sha256,
    string License);

public static class ModelCatalog
{
    public static readonly ModelArtifact FastAndAccurate = new(
        ModelKind.FastAndAccurate,
        "isnet-general-use-q8.onnx",
        new Uri("https://huggingface.co/onnx-community/ISNet-general-use-ONNX/resolve/main/model_quantized.onnx"),
        44_436_071,
        "feed6f32a5e707ca7e939576b2d891b23fb9eb4114749657a5efc64e8651e43a",
        "Apache-2.0");

    public static readonly ModelArtifact HighestQuality = new(
        ModelKind.HighestQuality,
        "birefnet-general.onnx",
        new Uri("https://huggingface.co/onnx-community/BiRefNet-general/resolve/main/onnx/model_fp16.onnx"),
        0,
        "",
        "MIT");

    public static IReadOnlyList<ModelArtifact> All { get; } = new[] { FastAndAccurate, HighestQuality };
}

public sealed class FileModelCatalog : IModelCatalog
{
    private readonly string _modelDirectory;
    private readonly Dictionary<ModelKind, ModelArtifact> _artifacts;

    public FileModelCatalog(string modelDirectory, IEnumerable<ModelArtifact>? artifacts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        _modelDirectory = Path.GetFullPath(modelDirectory);
        _artifacts = (artifacts ?? ModelCatalog.All).ToDictionary(a => a.Kind);
    }

    public ModelDescriptor Resolve(ModelKind kind)
    {
        if (!_artifacts.TryGetValue(kind, out var artifact))
            throw new KeyNotFoundException($"No model catalog entry exists for {kind}.");
        var path = Path.Combine(_modelDirectory, artifact.FileName);
        return new ModelDescriptor(kind, path, artifact.Kind == ModelKind.FastAndAccurate ? "Fast & accurate" : "Highest quality", artifact.Kind == ModelKind.FastAndAccurate, File.Exists(path));
    }

    public ModelArtifact GetArtifact(ModelKind kind) => _artifacts.TryGetValue(kind, out var artifact)
        ? artifact
        : throw new KeyNotFoundException($"No model catalog entry exists for {kind}.");
}

public sealed class ModelDownloader(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<string> DownloadAsync(ModelArtifact artifact, string destinationDirectory, IProgress<ProcessingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (string.IsNullOrWhiteSpace(artifact.Sha256) || artifact.Length <= 0)
            throw new InvalidOperationException("A model artifact must declare a positive length and SHA-256 checksum.");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, artifact.FileName);
        var partial = destination + ".partial";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUri);
        if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                existing = 0;
                using var restart = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUri);
                response.Dispose();
                response = await _httpClient.SendAsync(restart, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await WriteBodyAsync(body, partial, existing, artifact, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
        if (new FileInfo(partial).Length != artifact.Length || !string.Equals(await ComputeSha256Async(partial, cancellationToken), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded model failed its length or SHA-256 verification.");
        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private static async Task WriteBodyAsync(Stream body, string partial, long existing, ModelArtifact artifact, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var total = existing;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            progress?.Report(new ProcessingProgress("Downloading model", (double)total / artifact.Length));
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
