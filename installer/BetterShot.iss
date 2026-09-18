#define MyAppName "BetterShot"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\installer-publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{2D73E074-7582-4AAA-B5C7-5EB91C343A38}
AppName={#MyAppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AppMutex=Local\BetterShot.SingleInstance
SetupIconFile=..\BetterShot\Assets\BetterShot.ico
UninstallDisplayIcon={app}\BetterShot.exe
OutputDir={#OutputDir}
OutputBaseFilename=BetterShot-{#AppVersion}-Setup-x64
Compression=lzma2/fast
LZMADictionarySize=8192
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BetterShot"; Filename: "{app}\BetterShot.exe"
Name: "{autodesktop}\BetterShot"; Filename: "{app}\BetterShot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BetterShot.exe"; Description: "Launch BetterShot"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userstartup}\BetterShot.lnk"