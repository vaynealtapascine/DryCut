using BackgroundCut.Domain.Models;

namespace BackgroundCut.Domain.Tests;

public sealed class DomainTests
{
    [Theory]
    [InlineData("photo.jpg", "photo-background-removed.png")]
    [InlineData("photo.png", "photo-background-removed.png")]
    public void CreateCandidateUsesTransparentPngName(string source, string expected) =>
        Assert.Equal(expected, ExportNaming.CreateCandidate(source));

    [Fact]
    public void CreateUniquePathAddsCollisionSuffixWithoutOverwriting()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("out", "photo-background-removed.png"),
            Path.Combine("out", "photo-background-removed (2).png")
        };
        var result = ExportNaming.CreateUniquePath("out", "photo.jpg", existing.Contains);
        Assert.Equal(Path.Combine("out", "photo-background-removed (3).png"), result);
    }

    [Theory]
    [InlineData("C:/images/a.jpg", true)]
    [InlineData("C:/images/a.webp", true)]
    [InlineData("a.jpg", false)]
    [InlineData("C:/images/a.gif", false)]
    public void ImageInputValidatesSupportedLocalFiles(string path, bool expected) =>
        Assert.Equal(expected, new ImageInput(path).IsSupportedLocalFile);

    [Fact]
    public void DefaultsAreSafeAndPredictable()
    {
        Assert.Equal(ModelKind.FastAndAccurate, new ProcessingOptions().Model);
        Assert.Equal(RefinementPreset.Balanced, RefinementSettings.Default.Preset);
        Assert.Equal(ExportPolicy.DefaultFolder, ExportSettings.Default.Policy);
    }
}
