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

[Code]
function IsHex32(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := Length(Value) = 32;
  if not Result then Exit;
  for I := 1 to Length(Value) do
    if Pos(Value[I], '0123456789abcdefABCDEF') = 0 then
    begin
      Result := False;
      Exit;
    end;
end;

function IsManagedArtifact(const FileName, Extension: String): Boolean;
var
  Stem: String;
begin
  Result := CompareText(ExtractFileExt(FileName), Extension) = 0;
  if not Result then Exit;
  Stem := Copy(FileName, 1, Length(FileName) - Length(Extension));
  Result := IsHex32(Stem);
end;

function IsManagedTemporaryArtifact(const FileName: String): Boolean;
var
  Marker: Integer;
  DestinationName, TemporaryId: String;
begin
  Marker := Pos('.tmp-', Lowercase(FileName));
  Result := Marker > 1;
  if not Result then Exit;
  DestinationName := Copy(FileName, 1, Marker - 1);
  TemporaryId := Copy(FileName, Marker + 5, Length(FileName));
  Result := IsHex32(TemporaryId) and
    (IsManagedArtifact(DestinationName, '.png') or IsManagedArtifact(DestinationName, '.json'));
end;

procedure DeleteManagedHistory;
var
  HistoryDirectory, Path, Stem: String;
  FindData: TFindRec;
begin
  HistoryDirectory := ExpandConstant('{localappdata}\BackgroundCut\history');
  if not DirExists(HistoryDirectory) then Exit;

  if FindFirst(AddBackslash(HistoryDirectory) + '*.json', FindData) then
  begin
    try
      repeat
        if IsManagedArtifact(FindData.Name, '.json') then
        begin
          Path := AddBackslash(HistoryDirectory) + FindData.Name;
          Stem := Copy(FindData.Name, 1, Length(FindData.Name) - 5);
          DeleteFile(AddBackslash(HistoryDirectory) + Stem + '.png');
          DeleteFile(Path);
        end;
      until not FindNext(FindData);
    finally
      FindClose(FindData);
    end;
  end;

  if FindFirst(AddBackslash(HistoryDirectory) + '*.tmp-*', FindData) then
  begin
    try
      repeat
        if IsManagedTemporaryArtifact(FindData.Name) then
          DeleteFile(AddBackslash(HistoryDirectory) + FindData.Name);
      until not FindNext(FindData);
    finally
      FindClose(FindData);
    end;
  end;

  RemoveDir(HistoryDirectory);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    DeleteManagedHistory;
end;
