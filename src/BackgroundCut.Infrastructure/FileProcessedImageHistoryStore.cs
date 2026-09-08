using System.Text.Json;
using BackgroundCut.Application.Ports;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Infrastructure;

public sealed class FileProcessedImageHistoryStore : IProcessedImageHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _historyDirectory;

    public FileProcessedImageHistoryStore(string? historyDirectory = null)
    {
        _historyDirectory = Path.GetFullPath(historyDirectory ?? DefaultHistoryDirectory);
    }

    public static string DefaultHistoryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BackgroundCut",
        "history");

    public string HistoryDirectory => _historyDirectory;

    public async Task<ProcessedImageHistoryItem> SaveAsync(
        ProcessedImage image,
        string originalFileName,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_historyDirectory);
        var id = Guid.NewGuid();
        var pngFileName = id.ToString("N") + ".png";
        var item = new ProcessedImageHistoryItem(id, GetDisplayFileName(originalFileName), processedAtUtc, pngFileName);
        var pngPath = GetPngPath(id);
        var metadataPath = GetMetadataPath(id);
        var temporaryPngPath = CreateTemporaryPath(pngPath);
        var temporaryMetadataPath = CreateTemporaryPath(metadataPath);
        var pngCommitted = false;
        var metadataCommitted = false;

        try
        {
            await using (var stream = new FileStream(
                temporaryPngPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.SequentialScan))
            {
                await ImageSharpImageService.SavePngAsync(image, stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPngPath, pngPath);
            pngCommitted = true;

            await using (var stream = new FileStream(
                temporaryMetadataPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, item, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryMetadataPath, metadataPath);
            metadataCommitted = true;
            return item;
        }
        finally
        {
            DeleteIfPresent(temporaryPngPath);
            DeleteIfPresent(temporaryMetadataPath);
            if (pngCommitted && !metadataCommitted)
                DeleteIfPresent(pngPath);
        }
    }

    public Task<IReadOnlyList<ProcessedImageHistoryItem>> EnumerateMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<ProcessedImageHistoryItem>();
        if (!Directory.Exists(_historyDirectory))
            return Task.FromResult<IReadOnlyList<ProcessedImageHistoryItem>>(items);

        try
        {
            foreach (var path in Directory.EnumerateFiles(_historyDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryReadMetadata(path, out var item) && GetImagePath(item) is not null)
                    items.Add(item);
            }
        }
        catch (Exception exception) when (IsRecoverableFileException(exception))
        {
            // A partially readable history should not prevent the desktop app from starting.
        }

        items.Sort(static (left, right) =>
        {
            var timestampComparison = right.ProcessedAtUtc.CompareTo(left.ProcessedAtUtc);
            return timestampComparison != 0 ? timestampComparison : right.Id.CompareTo(left.Id);
        });
        return Task.FromResult<IReadOnlyList<ProcessedImageHistoryItem>>(items);
    }

    public async Task<ProcessedImage?> LoadAsync(
        ProcessedImageHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var path = TryResolvePngPath(item);
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            var decoded = await ImageSharpImageService.DecodeAsync(path, cancellationToken).ConfigureAwait(false);
            return new ProcessedImage(decoded.Pixels, decoded.Width, decoded.Height);
        }
        catch (Exception exception) when (IsRecoverableImageException(exception))
        {
            return null;
        }
    }

    public string? GetImagePath(ProcessedImageHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = TryResolvePngPath(item);
        return path is not null && File.Exists(path) ? path : null;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty)
            throw new ArgumentException("A history item must have a non-empty identity.", nameof(id));

        var metadataPath = GetMetadataPath(id);
        var pngPath = GetPngPath(id);
        if (TryReadMetadata(metadataPath, out var item))
            pngPath = TryResolvePngPath(item) ?? pngPath;

        DeleteArtifactIgnoringMissingDirectory(pngPath);
        DeleteArtifactIgnoringMissingDirectory(metadataPath);
        return Task.CompletedTask;
    }

    public Task<int> CleanupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_historyDirectory))
            return Task.FromResult(0);

        var cutoff = nowUtc.ToUniversalTime() - ProcessedImageHistoryPolicy.Retention;
        var metadataPaths = EnumerateFilesSafely("*.json").Where(IsOwnedMetadataFile).ToList();
        var deletedItems = 0;

        foreach (var metadataPath in metadataPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadMetadata(metadataPath, out var item))
            {
                if (IsOlderThan(metadataPath, cutoff))
                {
                    if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(metadataPath), "N", out var metadataId))
                        DeleteIfPresent(GetPngPath(metadataId));
                    DeleteIfPresent(metadataPath);
                }
                continue;
            }

            var pngPath = TryResolvePngPath(item) ?? GetPngPath(item.Id);
            if (!File.Exists(pngPath))
            {
                DeleteIfPresent(metadataPath);
                deletedItems++;
                continue;
            }
            if (item.ProcessedAtUtc < cutoff)
            {
                DeleteIfPresent(pngPath);
                DeleteIfPresent(metadataPath);
                deletedItems++;
            }
        }

        foreach (var path in EnumerateFilesSafely("*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOwnedTemporaryFile(path))
            {
                if (IsOlderThan(path, cutoff))
                    DeleteIfPresent(path);
                continue;
            }


        }

        return Task.FromResult(deletedItems);
    }

    private string GetMetadataPath(Guid id) => Path.Combine(_historyDirectory, id.ToString("N") + ".json");

    private string GetPngPath(Guid id) => Path.Combine(_historyDirectory, id.ToString("N") + ".png");

    private static string CreateTemporaryPath(string destination) => destination + ".tmp-" + Guid.NewGuid().ToString("N");

    private static string GetDisplayFileName(string originalFileName)
    {
        var normalized = originalFileName.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        var displayName = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return string.IsNullOrWhiteSpace(displayName) ? "image" : displayName;
    }

    private string? TryResolvePngPath(ProcessedImageHistoryItem item)
    {
        if (Path.IsPathRooted(item.PngPath) || !string.Equals(Path.GetExtension(item.PngPath), ".png", StringComparison.OrdinalIgnoreCase))
            return null;

        var expectedFileName = item.Id.ToString("N") + ".png";
        if (!string.Equals(item.PngPath, expectedFileName, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(_historyDirectory, item.PngPath));
            var relativePath = Path.GetRelativePath(_historyDirectory, fullPath);
            if (relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
                return null;
            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private bool TryReadMetadata(string path, out ProcessedImageHistoryItem item)
    {
        item = null!;
        try
        {
            var parsed = JsonSerializer.Deserialize<ProcessedImageHistoryItem>(File.ReadAllText(path), JsonOptions);
            if (parsed is null || TryResolvePngPath(parsed) is null)
                return false;
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(GetMetadataPath(parsed.Id)), StringComparison.OrdinalIgnoreCase))
                return false;
            item = parsed;
            return true;
        }
        catch (Exception exception) when (IsRecoverableMetadataException(exception))
        {
            return false;
        }
    }

    private List<string> EnumerateFilesSafely(string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(_historyDirectory, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception exception) when (IsRecoverableFileException(exception))
        {
            return [];
        }
    }

    private static bool IsOwnedMetadataFile(string path) => IsGuidArtifact(path, ".json");

    private static bool IsOwnedPngFile(string path) => IsGuidArtifact(path, ".png");

    private static bool IsGuidArtifact(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _);

    private static bool IsOwnedTemporaryFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var marker = fileName.LastIndexOf(".tmp-", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0 || !Guid.TryParseExact(fileName[(marker + 5)..], "N", out _))
            return false;

        var destinationName = fileName[..marker];
        return IsGuidArtifact(destinationName, ".png") || IsGuidArtifact(destinationName, ".json");
    }

    private static bool IsOlderThan(string path, DateTimeOffset cutoff)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime;
        }
        catch (Exception exception) when (IsRecoverableFileException(exception))
        {
            return false;
        }
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsRecoverableFileException(exception))
        {
            // Cleanup is best effort; a locked or disappearing artifact is safe to revisit later.
        }
    }


    // File.Delete already tolerates a missing FILE; it only throws DirectoryNotFoundException
    // when the containing directory itself is gone (e.g. the user cleared %LOCALAPPDATA%, or a
    // fresh store has never created its history folder). DeleteAsync's contract says missing
    // artifacts are ignored, so that case is swallowed here too. Anything else (a locked or
    // access-denied file) is intentionally left to propagate: MainViewModel.DeleteItemAsync
    // catches it and tells the user to close other apps and try again.
    private static void DeleteArtifactIgnoringMissingDirectory(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // The history directory does not exist; there is nothing to delete.
        }
    }

    private static bool IsRecoverableMetadataException(Exception exception) =>
        exception is JsonException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException;

    private static bool IsRecoverableImageException(Exception exception) =>
        exception is InvalidDataException or JsonException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException;

    private static bool IsRecoverableFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
