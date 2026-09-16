; installer.iss — офлайн-установщик WhiteMC для Windows x64.
; Собирается через Inno Setup 6 (ISCC.exe).
; Версия берётся из переменной окружения WHITEMC_VERSION,
; которую проставляет workflow из <Version> в csproj.

#define MyAppName "WhiteMC"
#define MyAppVersion GetEnv("WHITEMC_VERSION")
#define MyAppPublisher "aut1st1c"
#define MyAppURL "https://github.com/aut1st1c/WhiteMC"
#define MyAppExeName "WhiteMC.exe"

[Setup]
AppId={{7d7cbf14-6fcb-4736-b1bb-8e9c78604370}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

; --- Куда ставим ---
; {autopf} → %LocalAppData%\Programs  (при PrivilegesRequired=lowest)
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes

; --- Права ---
; "lowest" = без UAC, установка в пользовательскую папку.
; Диалог выбора прав отключён, чтобы лаунчер гарантированно
; мог писать рядом с собой (portable-поведение).
PrivilegesRequired=lowest

; --- Сборка ---
OutputDir=installer-out
OutputBaseFilename=WhiteMC-setup-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=Assets\whitemc.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; \
    GroupDescription: "Дополнительные задачи:"; Flags: unchecked

[Files]
; Всё содержимое publish/ — включая все .dll, .json, .exe
Source: "publish\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; \
    Description: "Запустить {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Удаляем только то, что положил установщик.
; Данные лаунчера (.whitemc внутри {app} в portable-режиме) тоже удалятся.
Type: filesandordirs; Name: "{app}"