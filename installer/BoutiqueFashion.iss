#define MyAppName "Bana Shop"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Bana Shop"
#define MyAppExeName "BanaShop.exe"

[Setup]
AppId={{0BC74935-4917-4A78-A54A-2788CBA55EC4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Bana Shop
DefaultGroupName={#MyAppName}
OutputDir=output
OutputBaseFilename=BanaShop-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\BoutiqueFashion.App\Assets\logo.ico
WizardImageFile=..\src\BoutiqueFashion.App\Assets\installer-side.png
WizardSmallImageFile=..\src\BoutiqueFashion.App\Assets\installer-small.png

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Lancer {#MyAppName}"; Flags: nowait postinstall skipifsilent

