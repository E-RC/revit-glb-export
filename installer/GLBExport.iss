; Inno Setup 6. Compilar: ISCC.exe /DAppVersion=1.0.0 installer\GLBExport.iss  (salida en dist\)
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6B0D4C1E-3F5A-4E0B-9C57-7A1D2E8F4B10}
AppName=GLB Export for Revit
AppVersion={#AppVersion}
AppPublisher=Archiplan
DefaultDirName={localappdata}\GLBExportInstaller
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
Uninstallable=yes
OutputDir=..\dist
OutputBaseFilename=GLBExport-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\GLBExport.extension\*"; DestDir: "{app}\GLBExport.extension"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"""; \
  StatusMsg: "Instalando pyRevit, Node.js y dependencias (puede tardar unos minutos)..."; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"""; Flags: runhidden waituntilterminated
