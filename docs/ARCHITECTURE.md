# BackgroundCut — Architecture

Status: implementation baseline  
Target: Windows 10 19041+ / Windows 11, x64  
Runtime: .NET 8 WPF, self-contained deployment

## 1. Product intent

BackgroundCut is a local-first desktop utility that removes image backgrounds without uploading images. It is designed for people who want a single obvious workflow—open or drop an image, remove the background, copy or save—while keeping advanced quality and export controls available but out of the way.

### Product principles

1. **Private by default:** inference and image processing happen locally.
2. **Fast by default:** use the bundled quantized IS-Net model and DirectML when available; fall back to CPU automatically.
3. **Quality on demand:** offer optional BiRefNet FP16 as a stronger, larger model and adjustable edge refinement.
4. **Recoverable interactions:** no destructive overwrite; clear errors; cancellation; settings persist.
5. **Native Windows integration:** drag/drop, clipboard, file picker, Explorer context menu, one-file installer.
6. **No technical setup:** self-contained runtime and bundled default model.

## 2. Chosen stack

- **UI:** WPF on .NET 8 (`net8.0-windows`), MVVM without a framework.
- **Inference:** Microsoft ONNX Runtime DirectML with automatic CPU fallback.
- **Image processing:** SixLabors.ImageSharp for decode/resize/mask/composition/PNG output; WPF imaging only at the UI boundary.
- **Settings:** versioned JSON under `%LocalAppData%\BackgroundCut\settings.json`; processed-image history under `%LocalAppData%\BackgroundCut\history`.
- **Models:** bundled IS-Net q8 (Apache-2.0); optional BiRefNet FP16 (MIT upstream, downloaded after explicit user action).
- **Installer:** Inno Setup, per-user by default, producing one signed-ready `.exe`; includes application, .NET runtime, default model, licenses, and optional Explorer integration task.
- **Tests:** xUnit for core/application logic plus a deterministic fake inference service; smoke test against the real ONNX model.

This stack avoids Python/runtime installation, browser shells, services, and network dependencies during ordinary use.

## 3. Clean architecture boundaries

```text
BackgroundCut.Domain
  Pure value types and policies: export destination, model choice,
  refinement settings, operation result. No UI, filesystem, registry,
  image library, or ONNX references.

BackgroundCut.Application
  Use cases and ports: process image, export, settings, model catalog,
  context-menu management, and processed-image history contracts. Depends only on Domain.

BackgroundCut.Infrastructure
  ONNX inference, ImageSharp processing, filesystem, clipboard bridge,
  registry integration, JSON settings, model download/checksum, and atomic PNG/metadata history storage.
  Implements Application ports.

BackgroundCut.Desktop
  WPF composition root, views, view-models, dialogs, drag/drop, progress,
  single-instance/command-line handoff. Depends on Application and
  Infrastructure.
```

### Dependency rule

Dependencies point inward. Domain is dependency-free. Application cannot reference WPF, ImageSharp, ONNX Runtime, registry APIs, or concrete filesystem paths.

## 4. Runtime flow

1. The desktop app accepts a path from file picker, drag/drop, command line, or Explorer context menu.
2. `MainViewModel` validates that the path is a supported local image and requests processing.
3. `RemoveBackgroundUseCase` resolves the selected installed model and calls `IBackgroundRemovalEngine` on a background thread with cancellation and progress.
4. Infrastructure:
   - decodes and corrects EXIF orientation;
   - converts RGB pixels into the model-specific NCHW tensor;
   - runs ONNX (DirectML first, CPU fallback);
   - normalizes/resizes the alpha mask;
   - applies optional edge refinement;
   - composes an RGBA image at the original dimensions.
5. The result is retained in memory for preview, clipboard, and export, then persisted as a transparent PNG with metadata in the local history folder.
6. History metadata can be enumerated newest-first without reading image pixels. The desktop gallery may load only its first 100 entries for rendering and loads full-resolution PNG data on demand.
7. The selected export policy determines the destination; generated names never overwrite existing files.

## 5. Model profiles

### Fast & accurate (default, bundled)

- IS-Net general-use q8
- Input: `input`, float32 `[1,3,1024,1024]`
- Normalize: `(pixel - 128) / 256`
- Output: `output`, float saliency mask
- Size: 44,436,071 bytes
- SHA-256: `feed6f32a5e707ca7e939576b2d891b23fb9eb4114749657a5efc64e8651e43a`
- License: Apache-2.0

### Highest quality (optional)

- BiRefNet FP16 ONNX
- Input: RGB normalized with ImageNet mean/std at 1024²
- Output: sigmoid alpha matte
- Approximate download: 490 MB
- License: MIT upstream. Exact downloaded file is checksum-verified against the catalog before activation.

Model definitions are data (`ModelDescriptor`) rather than branching UI logic. A failed optional download leaves the bundled model usable.

## 6. Edge refinement

Expose one approachable control with four presets:

- **None:** raw model mask.
- **Soft:** light edge-preserving feather.
- **Balanced (default):** small guided color-aware refinement plus one-pixel decontamination.
- **Detailed:** higher-resolution tiled guided refinement, stronger foreground-color decontamination, slower.

The implementation must avoid globally blurring the matte. Refinement works only in the uncertain alpha band, preserving solid foreground and transparent background.

## 7. Export semantics

Available actions:

- **Copy image:** place a PNG-capable image and Windows bitmap representation on the clipboard.
- **Save:** according to one of three persisted policies:
  - default folder (`Pictures\BackgroundCut` by default, configurable);
  - source image folder;
  - ask every time.
- **Save as…:** always prompts, regardless of policy.

PNG is the default because transparency is required. WebP is optional when its encoder preserves alpha. JPEG export is deliberately omitted from the first usable release because it cannot represent transparency.

Collision behavior: `photo-background-removed.png`, then `photo-background-removed (2).png`, etc. Never overwrite silently.

## 8. Explorer integration

The installer offers an unchecked/clearly described optional task, and Settings exposes Add/Remove buttons. Registration is per-user under `HKCU\Software\Classes\SystemFileAssociations\image\shell\BackgroundCut` so elevation is unnecessary. The command is:

```text
"<install>\BackgroundCut.exe" "%1"
```

The application treats command-line input as untrusted: it accepts exactly one supported local file, canonicalizes the path, and displays a normal error for invalid input.

## 9. Single-instance behavior

One process owns a named mutex. Later launches send the canonical path over a named pipe and exit. The first process brings its window forward and loads the new image. If handoff fails, the later process may start normally rather than dropping the request.

## 10. Performance and resilience

- Perform decode, inference, and refinement off the UI thread.
- Cache one active inference session; dispose it when the model changes.
- Prefer DirectML device 0; retry once with CPU if provider/session creation or inference fails.
- Bound image dimensions and decoded pixel count to prevent accidental memory exhaustion; show a useful message.
- Cancellation is cooperative between phases; inference itself may complete before cancellation is observed.
- Use atomic temp-file + move for model downloads and settings writes.
- Persist each successful transparent result as an atomic PNG plus metadata pair; retain history for 30 days.
- Verify model length and SHA-256 before loading.
- Do not log image content or paths by default; history metadata stores only the original display filename, not the source path.

## 11. Security and privacy

- No telemetry, accounts, image upload, background service, shell command construction, or arbitrary URL input.
- HTTPS-only model URLs pinned in a static catalog; checksums mandatory.
- Registry writes are limited to the app’s own HKCU key.
- Settings are non-secret local JSON.
- Installer and binaries are ready for Authenticode signing, but this local build is unsigned and Windows may show SmartScreen.

## 12. Packaging layout

```text
artifacts/
  BackgroundCut-Setup-<version>.exe   # distributable one-file installer
  BackgroundCut-portable-<version>.zip
  checksums.txt
```

The installed layout is conventional multi-file self-contained .NET output because ONNX Runtime has native DLLs. The requirement for “one file” applies to the distributable installer, not the post-install folder.

## 13. Acceptance criteria

- Installs on a clean supported x64 Windows machine without .NET/Python preinstalled.
- Opens JPG/PNG/WebP/BMP/TIFF from picker, drag/drop, command line, and Explorer context menu.
- Produces a same-size RGBA result with nontrivial transparency using the bundled model.
- UI remains responsive and exposes cancel/progress/error states.
- Copy and all three save policies work without silent overwrite.
- Model selection and all refinement presets persist.
- Optional model download is user-initiated, progress-reporting, checksum-verified, and recoverable.
- Uninstall removes registered context menu entries and app files but does not unexpectedly delete user exports.
