; Inno Setup — CodexQuotaSetup-{version}-{arch}.exe
; CI: pass absolute /DPublishDir (Inno resolves relative paths from this script's folder).

#ifndef MyAppName
  #define MyAppName "CodexQuota"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "CodexQuota.exe"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\CodexQuota.App\bin\x64\Release\net9.0-windows10.0.19041.0\win-x64\publish"
#endif
#ifndef MyAppVersion
  ; Single source of truth: read the version stamped into the published CodexQuota.exe
  ; (csproj <Version>; CI overrides this with /DMyAppVersion=<tag>). GetVersionNumbersString
  ; returns "X.Y.Z.0", so strip the trailing ".0".
  #define _VersionFull GetVersionNumbersString(AddBackslash(PublishDir) + MyAppExeName)
  #define MyAppVersion Copy(_VersionFull, 1, Len(_VersionFull) - 2)
#endif
#ifndef TargetArch
  #define TargetArch "x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#if TargetArch == "arm64"
  #define ArchAllowed "arm64"
  #define ArchInstallMode "arm64"
#else
  #define ArchAllowed "x64compatible"
  #define ArchInstallMode "x64compatible"
#endif

[Setup]
AppId={{B1317D25-244F-436A-9B5B-379406BCCF06}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Julien Maille
AppPublisherURL=https://github.com/JulienMaille/CodexQuota
AppSupportURL=https://github.com/JulienMaille/CodexQuota/issues
AppUpdatesURL=https://github.com/JulienMaille/CodexQuota/releases
DefaultDirName={autopf}\CodexQuota
DefaultGroupName=CodexQuota
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=CodexQuotaSetup-{#MyAppVersion}-{#TargetArch}
SetupIconFile=..\src\CodexQuota.App\Assets\CodexQuota.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchInstallMode}
MinVersion=10.0.19041
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
