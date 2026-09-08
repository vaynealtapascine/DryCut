using System.Text.Json;
using BackgroundCut.Domain.Models;

namespace BackgroundCut.Infrastructure.Tests;

public sealed class ProcessedImageHistoryStoreTests
{
    [Fact]
    public async Task SaveAndEnumerateReturnsMetadataNewestFirstWithoutSourcePath()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new FileProcessedImageHistoryStore(directory);
            var older = await store.SaveAsync(
                OnePixel(1, 2, 3, 255),
                @"C:\private\older.png",
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var newer = await store.SaveAsync(
                OnePixel(4, 5, 6, 128),
                "newer.png",
                new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero));

            var items = await store.EnumerateMetadataAsync();

            Assert.Equal(newer.Id, items[0].Id);
            Assert.Equal(older.Id, items[1].Id);
            Assert.Equal("older.png", older.OriginalFileName);
            var metadata = File.ReadAllText(Path.Combine(directory, older.Id.ToString("N") + ".json"));
            Assert.DoesNotContain(@"C:\private", metadata, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".png", older.PngPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadRoundTripsRgbaIncludingAlpha()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new FileProcessedImageHistoryStore(directory);
            var expected = new ProcessedImage(
                [10, 20, 30, 0, 40, 50, 60, 127, 70, 80, 90, 255, 100, 110, 120, 1],
                2,
                2);
            var item = await store.SaveAsync(expected, "alpha.png", DateTimeOffset.UtcNow);

            var actual = await store.LoadAsync(item);

            Assert.NotNull(actual);
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CleanupDeletesStrictlyOlderThanThirtyDaysAndKeepsBoundary()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new FileProcessedImageHistoryStore(directory);
            var now = new DateTimeOffset(2025, 2, 1, 12, 0, 0, TimeSpan.Zero);
            var boundary = await store.SaveAsync(OnePixel(1, 2, 3, 4), "boundary.png", now.AddDays(-30));
            var expired = await store.SaveAsync(OnePixel(5, 6, 7, 8), "expired.png", now.AddDays(-30).AddTicks(-1));

            var deleted = await store.CleanupAsync(now);
            var remaining = await store.EnumerateMetadataAsync();

            Assert.Equal(1, deleted);
            Assert.Contains(remaining, item => item.Id == boundary.Id);
            Assert.DoesNotContain(remaining, item => item.Id == expired.Id);
            Assert.True(File.Exists(Path.Combine(directory, boundary.PngPath)));
            Assert.False(File.Exists(Path.Combine(directory, expired.PngPath)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task EnumerationAndCleanupTolerateCorruptMetadataAndOrphanFiles()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new FileProcessedImageHistoryStore(directory);
            var now = new DateTimeOffset(2025, 2, 1, 12, 0, 0, TimeSpan.Zero);
            var valid = await store.SaveAsync(OnePixel(1, 2, 3, 255), "valid.png", now);
            var corruptMetadata = Path.Combine(directory, "corrupt.json");
            var orphanPng = Path.Combine(directory, "orphan.png");
            await File.WriteAllTextAsync(corruptMetadata, "{ definitely not metadata");
            await File.WriteAllBytesAsync(orphanPng, [1, 2, 3]);
            File.SetLastWriteTimeUtc(corruptMetadata, now.AddDays(-31).UtcDateTime);
            File.SetLastWriteTimeUtc(orphanPng, now.AddDays(-31).UtcDateTime);

            var items = await store.EnumerateMetadataAsync();
            var deleted = await store.CleanupAsync(now);

            Assert.Single(items);
            Assert.Equal(valid.Id, items[0].Id);
            Assert.Equal(0, deleted);
            Assert.False(File.Exists(corruptMetadata));
            Assert.False(File.Exists(orphanPng));
            Assert.NotNull(await store.LoadAsync(valid));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DeleteRemovesArtifactsAndIsIdempotent()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new FileProcessedImageHistoryStore(directory);
            var item = await store.SaveAsync(OnePixel(1, 2, 3, 4), "delete-me.png", DateTimeOffset.UtcNow);
            var pngPath = Path.Combine(directory, item.PngPath);
            var metadataPath = Path.Combine(directory, item.Id.ToString("N") + ".json");

            await store.DeleteAsync(item.Id);
            await store.DeleteAsync(item.Id);

            Assert.False(File.Exists(pngPath));
            Assert.False(File.Exists(metadataPath));
            Assert.Empty(await store.EnumerateMetadataAsync());
            Assert.Null(await store.LoadAsync(item));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task EnumerationRejectsForeignReferencesAndMissingArtifacts()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new FileProcessedImageHistoryStore(directory);
            var valid = await store.SaveAsync(OnePixel(1, 2, 3, 255), "valid.png", DateTimeOffset.UtcNow);
            var missing = await store.SaveAsync(OnePixel(4, 5, 6, 255), "missing.png", DateTimeOffset.UtcNow);
            File.Delete(Path.Combine(directory, missing.PngPath));

            var forgedId = Guid.NewGuid();
            var forged = new ProcessedImageHistoryItem(forgedId, "forged.png", DateTimeOffset.UtcNow, valid.PngPath);
            await File.WriteAllTextAsync(
                Path.Combine(directory, forgedId.ToString("N") + ".json"),
                JsonSerializer.Serialize(forged));

            var items = await store.EnumerateMetadataAsync();

            Assert.Single(items);
            Assert.Equal(valid.Id, items[0].Id);
            Assert.NotNull(store.GetImagePath(valid));
            Assert.Null(store.GetImagePath(forged));

            await store.CleanupAsync(DateTimeOffset.UtcNow);
            Assert.False(File.Exists(Path.Combine(directory, missing.Id.ToString("N") + ".json")));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static ProcessedImage OnePixel(byte red, byte green, byte blue, byte alpha) =>
        new([red, green, blue, alpha], 1, 1);

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BackgroundCut-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
