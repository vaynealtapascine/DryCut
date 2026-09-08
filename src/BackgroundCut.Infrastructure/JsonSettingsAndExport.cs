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
    private readonly string _uiPath;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackgroundCut", "settings.json");
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        _uiPath = Path.Combine(directory, "ui-settings.json");
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
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    public async Task<UiSettings> LoadUiSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_uiPath)) return UiSettings.Default;
        try
        {
            await using var stream = File.OpenRead(_uiPath);
            return await JsonSerializer.DeserializeAsync<UiSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? UiSettings.Default;
        }
        catch (JsonException)
        {
            return UiSettings.Default;
        }
    }

    public async Task SaveUiSettingsAsync(UiSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(Path.GetFullPath(_uiPath))!;
        Directory.CreateDirectory(directory);
        var temporary = _uiPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _uiPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            throw;
        }
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
            ExportPolicy.DefaultFolder => string.IsNullOrWhiteSpace(request.DefaultFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BackgroundCut")
                : request.DefaultFolder,
            ExportPolicy.SourceFolder => request.SourceFolder,
            ExportPolicy.AskEveryTime => Path.GetDirectoryName(request.RequestedPath),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Policy, "Unknown export policy.")
        };
        var requestedName = request.RequestedPath is not null
            ? Path.GetFileName(request.RequestedPath)
            : string.IsNullOrWhiteSpace(request.SuggestedFileName)
                ? "image-background-removed.png"
                : Path.GetFileName(request.SuggestedFileName);
        if (string.IsNullOrWhiteSpace(folder) || (request.Policy == ExportPolicy.AskEveryTime && string.IsNullOrWhiteSpace(request.RequestedPath)))
            throw new InvalidOperationException("An export destination is required.");
        Directory.CreateDirectory(folder);
        // When the user picked an exact path via "Save as..." (AskEveryTime), the OS save dialog already
        // owns the overwrite decision - honour that path exactly rather than silently redirecting to a
        // collision-safe "name (2).png". The collision-safe suffix only applies to the automatic
        // DefaultFolder/SourceFolder policies, where there was no user-facing overwrite prompt.
        var honoursRequestedPath = request.Policy == ExportPolicy.AskEveryTime && request.RequestedPath is not null;
        var destination = honoursRequestedPath
            ? request.RequestedPath!
            : CreateCollisionSafePath(folder, requestedName);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await ImageSharpImageService.SavePngAsync(image, stream, cancellationToken).ConfigureAwait(false);
            // Only a path the user chose in the OS save dialog may replace an existing file - they
            // already confirmed that overwrite there. A generated collision-safe name must never
            // overwrite, so a file appearing at that path since the check is left to throw rather
            // than be silently clobbered.
            File.Move(temporary, destination, overwrite: honoursRequestedPath);
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
