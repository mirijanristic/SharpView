; ============================================================================
;  SharpView - installer
;
;  Build:
;    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.0.0 installer\SharpView.iss
;
;  Ocekuje framework-dependent build u .\publish :
;    dotnet publish src/SharpView/SharpView.csproj -c Release -r win-x64 `
;      --self-contained false -p:PublishReadyToRun=true -o publish
;
;  Ako .NET 10 Desktop Runtime nije prisutan, instaler ga preuzme i instalira.
;  Zahtijeva Inno Setup 6.3 ili noviji (CreateDownloadPage + x64compatible).
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName        "SharpView"
#define MyAppPublisher   "Mirijan Ristic"
#define MyAppURL         "https://github.com/mirijanristic/SharpView"
#define MyAppExeName     "SharpView.exe"
#define MyProgId         "SharpView.Image"
#define PublishDir       "..\publish"

; Verzija runtimea koju aplikacija zahtijeva
#define DotNetMajor      "10"
#define DotNetUrl        "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

; VersionInfoVersion mora biti cisto numericki (x.y.z) - ako verzija ima
; sufiks poput 1.2.0-beta, za version info se koristi samo numericki dio.
#if Pos("-", MyAppVersion) > 0
  #define MyFileVersion Copy(MyAppVersion, 1, Pos("-", MyAppVersion) - 1)
#else
  #define MyFileVersion MyAppVersion
#endif

[Setup]
; Identitet aplikacije kroz sve buduce verzije.
; NE mijenjaj nakon prvog objavljenog release-a - inace se update instalira
; kao zasebna kopija umjesto da zamijeni postojecu.
AppId={{7C4F1B92-3A5E-4D18-9C6A-2E8B4F0D71A3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyFileVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=SharpView-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\SharpView\app.ico

; Instalacija runtimea trazi admina, pa nema smisla nuditi per-user rezim -
; korisnik bi inace dobio dva UAC prompta umjesto jednog.
PrivilegesRequired=admin

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associate";   Description: "Register SharpView as an option for image files"; GroupDescription: "File associations:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Runtime prvi, aplikacija poslije. [Run] stavke se izvrsavaju redom.
; Svaka stavka MORA biti u jednom redu - Inno Setup nema nastavak reda u sekcijama.
Filename: "{tmp}\dotnet-desktop-runtime.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET {#DotNetMajor} Desktop Runtime..."; Check: RuntimeInstallerDownloaded; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
; ---------------------------------------------------------------------------
; ProgID - opisuje KAKO SharpView otvara sliku.
; Registruje se uvijek; sam po sebi ne mijenja korisnikov default.
; ---------------------------------------------------------------------------
Root: HKA; Subkey: "Software\Classes\{#MyProgId}"; ValueType: string; ValueData: "Image File"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#MyProgId}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\{#MyProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; ---------------------------------------------------------------------------
; Registracija aplikacije - ovim se SharpView pojavljuje u "Open with" listi.
; ---------------------------------------------------------------------------
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpg";  ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpeg"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".png";  ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".bmp";  ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".gif";  ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tif";  ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tiff"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".webp"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".ico";  ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".heic"; ValueData: ""

; ---------------------------------------------------------------------------
; Capabilities - ovim SharpView ulazi u Settings > Default apps, gdje ga
; korisnik moze postaviti kao podrazumijevani. Windows 10+ ne dozvoljava da to
; instaler uradi umjesto njega (default handler je zasticen hesom).
; ---------------------------------------------------------------------------
Root: HKA; Subkey: "Software\{#MyAppName}"; Tasks: associate; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Tasks: associate; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Fast GPU-accelerated image viewer for Windows"; Tasks: associate

Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpg";  ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpeg"; ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".png";  ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".bmp";  ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".gif";  ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tif";  ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tiff"; ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webp"; ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ico";  ValueData: "{#MyProgId}"; Tasks: associate
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".heic"; ValueData: "{#MyProgId}"; Tasks: associate

Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: "Software\{#MyAppName}\Capabilities"; Tasks: associate; Flags: uninsdeletevalue

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; ============================================================================
[Code]
var
  DownloadPage: TDownloadWizardPage;
  RuntimeChecked: Boolean;
  RuntimeMissing: Boolean;

{ Trazi bilo koju instaliranu verziju Microsoft.WindowsDesktop.App cija se
  glavna verzija poklapa sa trazenom. Rezultat se kesira jer se funkcija
  poziva i iz [Run] Check parametra. }
function DetectDotNetRuntime: Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
  Prefix: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  Prefix := '{#DotNetMajor}' + '.';

  if not DirExists(BasePath) then
    Exit;

  if FindFirst(BasePath + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
            if Pos(Prefix, FindRec.Name) = 1 then
            begin
              Result := True;
              Break;
            end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function NeedsDotNetRuntime: Boolean;
begin
  if not RuntimeChecked then
  begin
    RuntimeMissing := not DetectDotNetRuntime;
    RuntimeChecked := True;
  end;
  Result := RuntimeMissing;
end;

{ [Run] stavka za runtime se izvrsava samo ako je fajl stvarno preuzet -
  stiti od pada kada preuzimanje ne uspije a korisnik izabere nastavak. }
function RuntimeInstallerDownloaded: Boolean;
begin
  Result := NeedsDotNetRuntime and
            FileExists(ExpandConstant('{tmp}\dotnet-desktop-runtime.exe'));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    SetupMessage(msgWizardPreparing),
    'Setup is downloading the .NET {#DotNetMajor} Desktop Runtime, which SharpView requires.',
    nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if (CurPageID = wpReady) and NeedsDotNetRuntime then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('{#DotNetUrl}', 'dotnet-desktop-runtime.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        { Preuzimanje nije uspjelo - ponudi nastavak, jer korisnik runtime
          moze instalirati rucno sa microsoft.com }
        if SuppressibleMsgBox(
             'Could not download the .NET Desktop Runtime:' + #13#10#13#10 +
             GetExceptionMessage + #13#10#13#10 +
             'You can install it manually from https://dotnet.microsoft.com/download' + #13#10 +
             'Continue installing SharpView anyway?',
             mbError, MB_YESNO, IDYES) = IDNO then
          Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

{ ---------------------------------------------------------------------------
  Ciscenje per-user "Open with" kesa pri deinstalaciji.
  Windows u HKCU kesira kandidate nezavisno od HKLM registracije; uninstaller
  po difoltu brise samo ono sto je sam upisao, pa bi bez ovoga u listi ostala
  mrtva stavka. Cisti se samo nalog koji pokrece deinstalaciju - kes drugih
  korisnickih naloga nije dostupan.
  --------------------------------------------------------------------------- }
const
  FileExtsKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\';

procedure CleanExplorerCacheForExt(const Ext: String);
var
  Key, Data, ProgId: String;
  Names: TArrayOfString;
  I: Integer;
begin
  { OpenWithList - MRU vrijednosti (a, b, c...) cija je vrijednost ime exe-a }
  Key := FileExtsKey + Ext + '\OpenWithList';
  if RegGetValueNames(HKCU, Key, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(HKCU, Key, Names[I], Data) and
         (CompareText(Data, '{#MyAppExeName}') = 0) then
        RegDeleteValue(HKCU, Key, Names[I]);

  { OpenWithProgids - vrijednost sa imenom naseg ProgID-a }
  RegDeleteValue(HKCU, FileExtsKey + Ext + '\OpenWithProgids', '{#MyProgId}');

  { UserChoice - samo ako je bas SharpView bio default; tudji izbor se ne dira }
  Key := FileExtsKey + Ext + '\UserChoice';
  if RegQueryStringValue(HKCU, Key, 'ProgId', ProgId) and
     (Pos('sharpview', Lowercase(ProgId)) = 1) then
    RegDeleteKeyIncludingSubkeys(HKCU, Key);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    CleanExplorerCacheForExt('.jpg');
    CleanExplorerCacheForExt('.jpeg');
    CleanExplorerCacheForExt('.png');
    CleanExplorerCacheForExt('.bmp');
    CleanExplorerCacheForExt('.gif');
    CleanExplorerCacheForExt('.tif');
    CleanExplorerCacheForExt('.tiff');
    CleanExplorerCacheForExt('.webp');
    CleanExplorerCacheForExt('.ico');
    CleanExplorerCacheForExt('.heic');
  end;
end;
