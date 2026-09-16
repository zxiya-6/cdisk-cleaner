; ============================================================
;  C盘清理助手 v5.0.0 — Inno Setup 安装脚本
;  编译: ISCC.exe setup.iss （或运行 build-installer.ps1）
;  产物: out\C盘清理助手_v5.0.0_安装包_x64.exe
; ============================================================

#define MyAppName "C盘清理助手"
#define MyAppVersion "5.0.0"
#define MyAppPublisher "zxiya-6"
#define MyAppURL "https://github.com/zxiya-6/cdisk-cleaner"
#define MyAppExeName "C盘清理助手.exe"

[Setup]
AppId={{B7F3C2A9-5E64-4D18-9C0A-3F5D8E7A1B20}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion=5.0.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
InfoBeforeFile=安装前须知.txt
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
OutputDir=out
OutputBaseFilename=C盘清理助手_v{#MyAppVersion}_安装包_x64
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinese"; MessagesFile: "lang\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "完整安装（推荐）"
Name: "compact"; Description: "精简安装（仅主程序与文档）"
Name: "custom"; Description: "自定义安装"; Flags: iscustom

[Components]
Name: "main"; Description: "主程序 {#MyAppName}（必需）"; Types: full compact custom; Flags: fixed
Name: "docs"; Description: "使用文档与界面截图"; Types: full compact custom
Name: "cli"; Description: "命令行工具（供自动化与维护使用，约 70 MB）"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: main
Source: "payload\docs\*"; DestDir: "{app}\Docs"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: docs
Source: "..\publish\cli\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: cli

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\使用文档"; Filename: "{app}\Docs\index.html"; Components: docs
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { 结束正在运行的应用程序（如存在），避免卸载残留锁定文件 }
    Exec('taskkill.exe', '/f /im "{#MyAppExeName}" /t', '', SW_HIDE, ewWaitUntilTerminated, ResCode);

    { 交互式卸载时询问是否删除程序数据；静默卸载默认保留数据（安全优先） }
    if not UninstallSilent then
    begin
      if MsgBox('卸载完成后，是否同时删除程序数据？' + #13#10 + #13#10 +
                '包括：清理备份区、操作台账、日志与设置（位于 用户目录\AppData\Local\C盘清理助手）。' + #13#10 +
                '选择“否”将保留这些数据，下次安装时继续可用。' + #13#10 + #13#10 +
                '注意：数据删除后不可恢复。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{localappdata}\C盘清理助手'), True, True, True);
    end;
  end;
end;
