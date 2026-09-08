# DryCut.Desktop

The desktop project provides the Avalonia user interface, MVVM presentation layer, drag-and-drop and file dialogs, clipboard access, startup arguments, settings, and single-instance handoff.

`CompositionRoot.cs` connects the desktop shell to the Application and Infrastructure implementations. The common application path is cross-platform; Windows additionally enables DirectML acceleration and the optional Explorer context-menu integration, while unsupported integrations remain disabled on macOS and Linux.

The project targets `net8.0`. Use an explicit runtime identifier when publishing, for example:

```text
dotnet publish DryCut.Desktop.csproj -c Release -r win-x64 --self-contained false
dotnet publish DryCut.Desktop.csproj -c Release -r osx-arm64 --self-contained false
dotnet publish DryCut.Desktop.csproj -c Release -r linux-x64 --self-contained false
```
