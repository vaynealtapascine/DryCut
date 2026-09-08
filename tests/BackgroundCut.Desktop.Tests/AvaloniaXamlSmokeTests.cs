using Avalonia.Headless.XUnit;
using Xunit;

namespace BackgroundCut.Desktop.Tests;

public sealed class AvaloniaXamlSmokeTests
{
    [AvaloniaFact]
    public void MainWindowXamlLoads()
    {
        var window = new MainWindow();

        Assert.Equal("BackgroundCut", window.Title);
        Assert.Equal(1024, window.MinWidth);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsWindowXamlLoads()
    {
        var window = new SettingsWindow();

        Assert.Equal("BackgroundCut settings", window.Title);
        Assert.Equal(620, window.Width);
        window.Close();
    }
}
