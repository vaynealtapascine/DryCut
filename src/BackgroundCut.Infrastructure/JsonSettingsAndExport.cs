using System.Text.Json;
using System.Text.Json.Serialization;
using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackgroundCut", "settings.json");
    }

    public async Task<ExportSettings> LoadExportSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return ExportSettings.Default;
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<ExportSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? ExportSettings.Default;
        }
        catch (JsonException)
        {
            return ExportSettings.Default;
        }
    }

    public async Task SaveExportSettingsAsync(ExportSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed class PngExportService : IExportService
{
    public async Task<ExportedFile> ExportAsync(ProcessedImage image, ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(request);
        var folder = request.Policy switch
        {
            ExportPolicy.DefaultFolder => request.DefaultFolder,
            ExportPolicy.SourceFolder => request.SourceFolder,
            ExportPolicy.AskEveryTime => Path.GetDirectoryName(request.RequestedPath),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Policy, "Unknown export policy.")
        };
        var requestedName = request.RequestedPath is null ? "image-background-removed.png" : Path.GetFileName(request.RequestedPath);
        if (string.IsNullOrWhiteSpace(folder) || (request.Policy == ExportPolicy.AskEveryTime && string.IsNullOrWhiteSpace(request.RequestedPath)))
            throw new InvalidOperationException("An export destination is required.");
        Directory.CreateDirectory(folder);
        var destination = request.Policy == ExportPolicy.AskEveryTime && request.RequestedPath is not null
            ? request.RequestedPath
            : CreateCollisionSafePath(folder, requestedName);
        if (request.Policy == ExportPolicy.AskEveryTime && File.Exists(destination))
            destination = CreateCollisionSafePath(folder, requestedName);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await ImageSharpImageService.SavePngAsync(image, stream, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination);
            return new ExportedFile(destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string CreateCollisionSafePath(string folder, string requestedName)
    {
        var stem = Path.GetFileNameWithoutExtension(requestedName);
        var extension = ".png";
        var candidate = Path.Combine(folder, stem + extension);
        for (var suffix = 2; File.Exists(candidate); suffix++) candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");
        return candidate;
    }
}
