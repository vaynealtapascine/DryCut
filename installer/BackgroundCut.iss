; BackgroundCut per-user installer. Build with scripts/build-release.ps1.
#ifndef AppVersion
#define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
#define SourceDir "..\\installer\\staging"
#endif
#ifndef OutputDir
#define OutputDir "..\\artifacts"
#endif

[Setup]
AppId={{B6D8C2B2-4A4D-4EB0-9F72-8D6B6A4D1E21}
AppName=BackgroundCut
AppVersion={#AppVersion}
AppVerName=BackgroundCut {#AppVersion}
AppPublisher=BackgroundCut
DefaultDirName={localappdata}\Programs\BackgroundCut
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=BackgroundCut-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\BackgroundCut.ico
UninstallDisplayIcon={app}\BackgroundCut.Desktop.exe
ChangesAssociations=yes
MinVersion=10.0.19041
LicenseFile={#SourceDir}\LICENSE.txt
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "explorercontext"; Description: "Add 'Remove background with BackgroundCut' to image context menu"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\models\isnet-general-use-q8.onnx"; DestDir: "{app}\models"; Flags: ignoreversion
Source: "{#SourceDir}\models\birefnet-fp16.onnx"; DestDir: "{app}\models"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\USER_GUIDE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\BackgroundCut"; Filename: "{app}\BackgroundCut.Desktop.exe"
Name: "{autoprograms}\BackgroundCut user guide"; Filename: "{app}\USER_GUIDE.txt"
Name: "{autodesktop}\BackgroundCut"; Filename: "{app}\BackgroundCut.Desktop.exe"; Tasks: desktopicon

[Registry]
; Always register cleanup, including entries users enable later from Settings.
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\BackgroundCut"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\BackgroundCut"; ValueType: string; ValueName: ""; ValueData: "Remove background with BackgroundCut"; Flags: uninsdeletekey; Tasks: explorercontext
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\BackgroundCut"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\BackgroundCut.Desktop.exe"""; Tasks: explorercontext
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\BackgroundCut\command"; ValueType: string; ValueName: ""; ValueData: """{app}\BackgroundCut.Desktop.exe"" ""%1"""; Flags: uninsdeletekey; Tasks: explorercontext

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "{app}\BackgroundCut.Desktop.exe"; Description: "Launch BackgroundCut"; Flags: nowait postinstall skipifsilent
