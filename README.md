# BackgroundCut

BackgroundCut is a private, local Windows app for removing image backgrounds. Drop in a photo, review the transparent result, then copy it or save it as a PNG.

## Highlights

- **Fast by default:** an included, quantized IS-Net model handles everyday images offline.
- **Stronger when needed:** download the optional BiRefNet model in Settings for hair, fur, and busy backgrounds.
- **Adjustable edges:** choose None, Balanced, Smooth, or Detailed edge cleanup and reprocess without reopening the image.
- **Flexible output:** copy transparent PNG data to the clipboard, save to `Pictures\BackgroundCut`, save beside the original, or choose a location each time.
- **Windows-friendly:** drag and drop, standard file picker, single-instance file forwarding, and optional Explorer image context-menu integration.
- **Private:** inference runs on your PC. Images are not uploaded.

## Install

For most people, run:

```text
BackgroundCut-Setup-1.0.0.exe
```

The installer is per-user and does not require administrator access. The included default model works offline after installation. Because this build is not code-signed, Windows SmartScreen may show an unrecognized-app warning.

A portable ZIP is also available. Extract it completely, then run `BackgroundCut.Desktop.exe`. Do not run the executable from inside the ZIP.

## Use

1. Open or drop a PNG, JPEG, WebP, BMP, TIFF, or GIF image.
2. Wait for the transparent preview.
3. If needed, choose a stronger model or a different edge preset, then select **Apply quality changes**.
4. Choose **Copy**, **Save**, or **Save as…**.

The original image is never modified.

See `docs/USER_GUIDE.txt` for settings, Explorer integration, troubleshooting, and privacy details.

## Build from source

Requirements:

- Windows 10 19041 or later, or Windows 11
- .NET 8 SDK
- PowerShell 5.1+
- Inno Setup 6 (only for the installer)

```powershell
./scripts/build-release.ps1 -Version 1.0.0
```

The script restores, tests, publishes a self-contained `win-x64` build, downloads and SHA-256 verifies the default model, and creates:

- `artifacts/BackgroundCut-Setup-1.0.0.exe`
- `artifacts/BackgroundCut-portable-1.0.0.zip`

For a reproducible build using an already verified model:

```powershell
./scripts/build-release.ps1 -Version 1.0.0 -DefaultModelPath C:\path\to\isnet-general-use-q8.onnx
```

Architecture and implementation decisions are in `docs/ARCHITECTURE.md` and `docs/IMPLEMENTATION.md`.

## Models and licenses

- IS-Net general-use q8 is an Apache-2.0 licensed quantized ONNX redistribution from `SacredNoir/isnet-general-use-onnx`.
- BiRefNet ONNX is distributed by `onnx-community/BiRefNet-ONNX` under MIT metadata and is downloaded only when requested.
- Runtime dependency notices are generated into `THIRD-PARTY-NOTICES.txt` during release packaging.

BackgroundCut's source is MIT licensed. See `LICENSE`.
