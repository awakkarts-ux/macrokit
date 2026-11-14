; Установщик MacroKit (Inno Setup 6).
;
; Собирается в GitHub Actions (.github/workflows/release.yml) при пуше тега vX.Y.Z:
;   ISCC /DAppVersion=X.Y.Z installer\MacroKit.iss
; Результат — dist\MacroKit-Setup-X.Y.Z.exe, он прикладывается к релизу,
; и программа скачивает именно его, когда обновляется по кнопке.
;
; Ставится в профиль пользователя (без запроса прав администратора), поэтому
; обновление из программы проходит тихо. Настройки лежат отдельно, в
; %APPDATA%\MacroKit, — установщик и удаление их не трогают.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{B4005A38-5022-4625-8EDD-B0B2C52BC699}
AppName=MacroKit
AppVersion={#AppVersion}
AppVerName=MacroKit {#AppVersion}
AppPublisher=awakkarts-ux
AppPublisherURL=https://github.com/awakkarts-ux/macrokit
AppSupportURL=https://github.com/awakkarts-ux/macrokit/issues
AppUpdatesURL=https://github.com/awakkarts-ux/macrokit/releases
VersionInfoVersion={#AppVersion}.0
DefaultDirName={localappdata}\Programs\MacroKit
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=MacroKit-Setup-{#AppVersion}
SetupIconFile=..\icon.ico
UninstallDisplayIcon={app}\MacroKit.exe
UninstallDisplayName=MacroKit
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Обновление из программы: она закрывается сама, но если ещё жива —
; установщик закроет её через Restart Manager
CloseApplications=force
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Ярлыки:"

[Files]
Source: "..\bin\MacroKit.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\fonts\ui\OFL-Onest.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\fonts\ui\OFL-JetBrainsMono.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MacroKit"; Filename: "{app}\MacroKit.exe"
Name: "{autodesktop}\MacroKit"; Filename: "{app}\MacroKit.exe"; Tasks: desktopicon

[Run]
; Без skipifsilent: при тихой установке (обновление из программы) новая
; версия запускается сама, при обычной — галочкой на последней странице
Filename: "{app}\MacroKit.exe"; Description: "Запустить MacroKit"; Flags: nowait postinstall
