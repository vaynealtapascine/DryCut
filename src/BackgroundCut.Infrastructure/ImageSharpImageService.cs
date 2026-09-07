using BackgroundCut.Domain.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BackgroundCut.Infrastructure;

public sealed class ImageSharpImageService
{
    private const long MaximumPixels = 100_000_000;

    public static async Task<DecodedImage> DecodeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = await Image.IdentifyAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The selected file is not a readable image.");
        if ((long)info.Width * info.Height > MaximumPixels)
            throw new InvalidDataException("This image is too large to process safely. Use an image smaller than 100 megapixels.");
        await using var stream = File.OpenRead(path);
        using var image = await Image.LoadAsync<Rgba32>(stream, cancellationToken).ConfigureAwait(false);
        image.Mutate(context => context.AutoOrient());
        return FromImage(image);
    }

    public static DecodedImage FromImage(Image<Rgba32> image)
    {
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var offset = (y * image.Width + x) * 4;
                    pixels[offset] = row[x].R;
                    pixels[offset + 1] = row[x].G;
                    pixels[offset + 2] = row[x].B;
                    pixels[offset + 3] = row[x].A;
                }
            }
        });
        return new DecodedImage(pixels, image.Width, image.Height);
    }

    public static ProcessedImage ComposeRgba(DecodedImage source, ReadOnlySpan<float> alpha)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (alpha.Length != source.Width * source.Height)
            throw new ArgumentException("The alpha mask must match the decoded image dimensions.", nameof(alpha));

        var pixels = new byte[source.Pixels.Length];
        for (var i = 0; i < source.Width * source.Height; i++)
        {
            var sourceOffset = i * 4;
            pixels[sourceOffset] = source.Pixels[sourceOffset];
            pixels[sourceOffset + 1] = source.Pixels[sourceOffset + 1];
            pixels[sourceOffset + 2] = source.Pixels[sourceOffset + 2];
            pixels[sourceOffset + 3] = (byte)Math.Clamp(MathF.Round(alpha[i] * 255f), 0, 255);
        }
        return new ProcessedImage(pixels, source.Width, source.Height);
    }

    public static async Task SavePngAsync(ProcessedImage image, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(destination);
        using var output = Image.LoadPixelData<Rgba32>(image.Pixels, image.Width, image.Height);
        await output.SaveAsPngAsync(destination, new PngEncoder(), cancellationToken).ConfigureAwait(false);
    }
}

public sealed record DecodedImage
{
    public DecodedImage(byte[] pixels, int width, int height)
    {
        Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA pixels must contain exactly width * height * 4 bytes.", nameof(pixels));
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
}
