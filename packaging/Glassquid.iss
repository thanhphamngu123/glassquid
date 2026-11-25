; User-scope x64 installer for the local Glassquid candidate.
#ifndef AppIdValue
  #error AppIdValue is required.
#endif
#ifndef ProductVersion
  #error ProductVersion is required.
#endif
#ifndef FileVersion
  #error FileVersion is required.
#endif
#ifndef PackageBaseName
  #error PackageBaseName is required.
#endif
#define StageDirectory GetEnv("GLASSQUID_PACKAGE_STAGE")
#if StageDirectory == ""
  #error GLASSQUID_PACKAGE_STAGE is required.
#endif

[Setup]
AppId={#AppIdValue}
AppName=Glassquid
AppVersion={#ProductVersion}
AppVerName=Glassquid {#ProductVersion}
DefaultDirName={localappdata}\Programs\Glassquid
DefaultGroupName=Glassquid
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UsePreviousAppDir=yes
UninstallDisplayName=Glassquid
UninstallDisplayIcon={app}\Glassquid.exe
SetupIconFile=..\src\Glassquid.App\Assets\Glassquid.ico
AppPublisher=Glassquid
VersionInfoProductName=Glassquid
VersionInfoProductVersion={#FileVersion}
VersionInfoVersion={#FileVersion}
VersionInfoTextVersion={#FileVersion}
VersionInfoProductTextVersion={#ProductVersion}
OutputBaseFilename={#PackageBaseName}-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo lối tắt trên màn hình nền"; GroupDescription: "Lối tắt bổ sung:"; Flags: unchecked

[Files]
Source: "{#StageDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Glassquid"; Filename: "{app}\Glassquid.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Glassquid"; Filename: "{app}\Glassquid.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1"; ValueName: "InstallLocation"; ValueType: string; ValueData: "{app}"; Flags: uninsdeletevalue
