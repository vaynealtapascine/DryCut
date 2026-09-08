# BackgroundCut — Implementation Plan

This document is the execution contract for the first complete Windows build.

## Phase 0 — repository baseline

- [ ] Create solution, source/test/installer/scripts structure.
- [ ] Add `.editorconfig`, `.gitignore`, `Directory.Build.props`, and `AGENTS.md`.
- [ ] Make atomic commits; no generated build outputs or model weights in Git.

## Phase 1 — domain and application core

- [ ] Define model/refinement/export enums and immutable records.
- [ ] Define ports: removal engine, image codec/compositor, settings, export, clipboard, model manager, Explorer integration.
- [ ] Implement remove-background and export use cases.
- [ ] Unit-test naming collisions, validation, settings defaults, and use-case orchestration.

## Phase 2 — infrastructure

- [ ] Implement ImageSharp decode with EXIF orientation and original-size RGBA composition.
- [ ] Implement generic ONNX profile preprocessing/postprocessing for IS-Net and BiRefNet.
- [ ] Attempt DirectML, with deterministic CPU fallback and readable diagnostics.
- [ ] Implement uncertain-band edge refinement presets.
- [ ] Implement atomic JSON settings and collision-safe exports.
- [ ] Implement atomic processed-image history PNG/metadata storage, newest-first metadata enumeration, on-demand loads, deletion, and 30-day cleanup.
- [ ] Implement checksum-verified resumable model download.
- [ ] Implement clipboard and HKCU Explorer context-menu adapters.
- [ ] Add real-model smoke test and synthetic mask/refinement tests.

## Phase 3 — desktop UX

- [ ] Build an accessible Avalonia window with keyboard navigation, high-DPI support, and a minimum 1024×680 layout.
- [ ] Empty state: large drop target plus “Choose image”.
- [ ] Working state: preview skeleton/progress text/cancel.
- [ ] Result state: before/after checkerboard preview, zoom-to-fit, Copy, Save, Save as…, new image.
- [ ] Compact quality controls: model and edge detail, with plain-language descriptions.
- [ ] Settings dialog: output policy, default folder, model management, acceleration status, Explorer integration.
- [ ] Accept path from command line and support single-instance handoff.
- [ ] Keep technical exception details behind a disclosure/copy-details action.

## Phase 4 — packaging

- [ ] Publish self-contained `win-x64` release.
- [ ] Fetch default model to staging and verify the documented SHA-256.
- [ ] Create an Inno Setup script with per-user install, shortcuts, optional context-menu task, clean uninstall, and upgrade support.
- [ ] Build a single setup EXE plus portable ZIP.
- [ ] Generate SHA-256 checksums and third-party notices.

## Phase 5 — verification

- [ ] `dotnet test` passes.
- [ ] Release publish succeeds from a clean output directory.
- [ ] Real-model CLI/smoke test produces a same-size transparent PNG.
- [ ] Launch desktop app and exercise picker/drop equivalent, processing, copy, save, settings, cancellation/error path, and command-line open.
- [ ] Install setup silently to a temporary per-user location, launch installed binary, verify files and registry integration, then uninstall and verify cleanup.
- [ ] Review package contents, licenses, and checksums.

## Workstream boundaries for delegated agents

Agents must commit only cohesive changes and must not change architecture without documenting the reason.

1. **Core agent:** solution scaffolding, Domain/Application, unit tests.
2. **Inference agent:** Infrastructure imaging, ONNX engine, refinement, model download, tests.
3. **Desktop agent:** Avalonia views/view-models, settings, clipboard, command-line/single-instance.
4. **Packaging agent:** publish scripts, Inno Setup, model staging, notices, verification scripts.

Parallel agents should use isolated Git worktrees. Integration happens only after each branch builds/tests in its own worktree.

## Definition of done

A source tree is not the deliverable. Done means a locally exercised installer exists under `artifacts/`, installs without external runtimes, launches, processes a real image with the bundled model, exports a transparent PNG, and uninstalls cleanly. Any unverified criterion is reported explicitly rather than implied.
