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
        new Uri("https://huggingface.co/SacredNoir/isnet-general-use-onnx/resolve/main/isnet-general-use-q8.onnx?download=true"),
        44_436_071,
        "feed6f32a5e707ca7e939576b2d891b23fb9eb4114749657a5efc64e8651e43a",
        "Apache-2.0");

    public static readonly ModelArtifact HighestQuality = new(
        ModelKind.HighestQuality,
        "birefnet-fp16.onnx",
        new Uri("https://huggingface.co/onnx-community/BiRefNet-ONNX/resolve/main/onnx/model_fp16.onnx?download=true"),
        489_666_272,
        "3654c741eb80bd926ada8fed1713b506ccf8d30eb1f6487e87eb9f234f33df09",
        "MIT");

    public static IReadOnlyList<ModelArtifact> All { get; } = new[] { FastAndAccurate, HighestQuality };
}

/// <summary>Resolves bundled models first and optional downloaded models from per-user storage.</summary>
public sealed class FileModelCatalog : IModelCatalog
{
    private readonly string _bundledModelDirectory;
    private readonly string _userModelDirectory;
    private readonly Dictionary<ModelKind, ModelArtifact> _artifacts;

    public FileModelCatalog(
        string bundledModelDirectory,
        string? userModelDirectory = null,
        IEnumerable<ModelArtifact>? artifacts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledModelDirectory);
        _bundledModelDirectory = Path.GetFullPath(bundledModelDirectory);
        _userModelDirectory = Path.GetFullPath(userModelDirectory ?? bundledModelDirectory);
        _artifacts = (artifacts ?? ModelCatalog.All).ToDictionary(artifact => artifact.Kind);
    }

    public string UserModelDirectory => _userModelDirectory;

    public ModelDescriptor Resolve(ModelKind kind)
    {
        var artifact = GetArtifact(kind);
        var path = GetInstalledPath(artifact);
        return new ModelDescriptor(
            kind,
            path,
            kind == ModelKind.FastAndAccurate ? "Fast & accurate" : "Highest quality",
            kind == ModelKind.FastAndAccurate,
            File.Exists(path));
    }

    public ModelArtifact GetArtifact(ModelKind kind) => _artifacts.TryGetValue(kind, out var artifact)
        ? artifact
        : throw new KeyNotFoundException($"No model catalog entry exists for {kind}.");

    public string GetDownloadPath(ModelKind kind) => Path.Combine(_userModelDirectory, GetArtifact(kind).FileName);

    public bool RemoveOptionalModel(ModelKind kind)
    {
        if (kind == ModelKind.FastAndAccurate)
            throw new InvalidOperationException("The bundled model cannot be removed from Settings.");
        var path = GetDownloadPath(kind);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string GetInstalledPath(ModelArtifact artifact)
    {
        var userPath = Path.Combine(_userModelDirectory, artifact.FileName);
        if (File.Exists(userPath)) return userPath;

        var bundledPath = Path.Combine(_bundledModelDirectory, artifact.FileName);
        if (File.Exists(bundledPath)) return bundledPath;

        // Portable development/release layout fallback: app\ executable beside ..\models.
        var siblingPath = Path.GetFullPath(Path.Combine(_bundledModelDirectory, "..", "models", artifact.FileName));
        return File.Exists(siblingPath) ? siblingPath : (artifact.Kind == ModelKind.FastAndAccurate ? bundledPath : userPath);
    }
}

public sealed class ModelDownloader(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<string> DownloadAsync(
        ModelArtifact artifact,
        string destinationDirectory,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (artifact.DownloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Model downloads must use HTTPS.");
        if (string.IsNullOrWhiteSpace(artifact.Sha256) || artifact.Length <= 0)
            throw new InvalidOperationException("A model artifact must declare a positive length and SHA-256 checksum.");

        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, artifact.FileName);
        if (File.Exists(destination) &&
            new FileInfo(destination).Length == artifact.Length &&
            string.Equals(await ComputeSha256Async(destination, cancellationToken), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            return destination;

        var partial = destination + ".partial";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0L;
        if (existing > artifact.Length)
        {
            File.Delete(partial);
            existing = 0;
        }

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

        if (new FileInfo(partial).Length != artifact.Length ||
            !string.Equals(await ComputeSha256Async(partial, cancellationToken), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException("The downloaded model failed its length or SHA-256 verification.");
        }

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private static async Task WriteBodyAsync(
        Stream body,
        string partial,
        long existing,
        ModelArtifact artifact,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            partial,
            existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var total = existing;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            progress?.Report(new ProcessingProgress("Downloading highest-quality model…", Math.Clamp((double)total / artifact.Length, 0, 1)));
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
