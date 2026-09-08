# DryCut release packaging

These scripts build the Windows distributables without adding model weights or
publish output to Git. Run them from a Windows machine with the .NET 8 SDK.
Inno Setup 6 or 7 is optional for dry runs and required for a real installer.
Python and a pre-installed .NET runtime are not required by the resulting app.

## Dry run

```powershell
pwsh -File .\scripts\build-release.ps1 -Version 0.1.0 -DryRun
```

Dry run checks the packaging flow and reports the expected publish/model/ZIP
steps without writing a model or compiling an installer.

## Build a release

```powershell
pwsh -File .\scripts\build-release.ps1 -Version 0.1.0
```

The build first deletes `installer/staging` and `artifacts`, then:

1. publishes `DryCut.Desktop.csproj` self-contained for `win-x64`;
2. downloads `isnet-general-use-q8.onnx` over HTTPS and verifies its exact
   documented length and SHA-256;
3. assembles `THIRD-PARTY-NOTICES.txt` from project/NuGet metadata and cached
   license texts;
4. creates a deterministic portable ZIP with fixed entry timestamps;
5. detects `ISCC.exe` under common per-user and system Inno Setup 6/7 paths;
6. compiles a per-user, self-contained installer; and
7. writes `artifacts/checksums.txt` for the ZIP and installer.

The installer is installed under `%LocalAppData%\Programs\DryCut`, uses
`PrivilegesRequired=lowest`, and includes the app's self-contained .NET files
and the default model. Its optional unchecked Explorer task registers only the
per-user `HKCU\Software\Classes\SystemFileAssociations\image` command. The
registry key and installed files are removed on uninstall; user exports are
not touched.

## Individual steps

The lower-level scripts can be run independently for troubleshooting:

- `download-model.ps1`
- `publish-win-x64.ps1`
- `assemble-notices.ps1`
- `make-portable-zip.ps1`
- `generate-checksums.ps1`

They use strict error handling and stop on missing inputs, failed commands, or
checksum mismatches. `publish-win-x64.ps1` deliberately fails if the Desktop
project does not produce `DryCut.Desktop.exe`; this prevents a plausible
but unusable release while the desktop implementation is incomplete.

## Expected output

```text
artifacts/
  DryCut-Setup-<version>.exe
  DryCut-portable-<version>.zip
  checksums.txt
```

Never commit `artifacts/`, `installer/staging/`, or model files. Review the
notices file and run a silent install/uninstall smoke test on a clean x64
Windows machine before signing or distributing the installer.
