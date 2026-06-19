; ==========================================================================
; 班级电脑控制助手 - 安装程序（自带 .NET 10 运行时）
; 作者: linhongyu3
; 版本: 2.0.0
; ==========================================================================

#define MyAppName      "班级电脑控制助手"
#define MyAppNameEn    "ClassroomControl"
#define MyAppVersion   "2.0.0"
#define MyAppPublisher "linhongyu3"
#define MyAppURL       "https://github.com/linhongyu3"
#define MyAppProjectURL "https://github.com/linhongyu3/ClassRoomControl"
#define MyAppExeName   "ClassroomControl.exe"
#define MySourceDir    "D:\Product\ClassRoomControl\publish-scd"
#define MySetupIcon    "D:\Product\图标.ico"

[Setup]
AppId={{8F2A7B1E-4D0E-4B7C-9E1F-1C2D3E4F5A02}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppProjectURL}
AppUpdatesURL={#MyAppProjectURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=D:\Product\ClassRoomControl\Output
OutputBaseFilename=ClassroomControl-v2.0.0-win-x64-Setup-自带运行时
SetupIconFile={#MySetupIcon}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
DisableProgramGroupPage=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCopyright=Copyright (C) 2026 linhongyu3
MinVersion=10.0.17763
InternalCompressLevel=max
DiskSpanning=no
; 允许用户选择为所有用户还是当前用户安装
PrivilegesRequiredOverridesAllowed=dialog
PrivilegesRequired=lowest

[Languages]
Name: "chinesesimplified"; MessagesFile: "D:\Product\ClassRoomControl\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked
Name: "startupicon"; Description: "开机自动启动"; GroupDescription: "自动运行:"; Flags: unchecked

[Files]
; 应用主文件
Source: "{#MySourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\*.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\*.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\logo.ico"; DestDir: "{app}"; Flags: ignoreversion
; 自包含运行时（所有文件和子目录）
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.dll;*.json;*.pdb;*.exe;*.ico"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\logo.ico"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\logo.ico"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
