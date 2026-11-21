; 文件卫士安装包脚本（Inno Setup 6）
; 构建前需要：
;   publish\app-full\FileGuard.exe       self-contained 发布产物
;   publish\service-full\FileGuard.Service.exe
;   installer\redist\winfsp-2.1.25156.msi
; 用 installer\build.ps1 一键完成。

#ifndef MyAppVersion
  #define MyAppVersion "0.1.1"
#endif

#define MyAppName "文件卫士"
#define MyAppPublisher "qxkt222"
#define MyAppURL "https://github.com/qxkt222/FileGuard-Windows"
#define MyAppExeName "FileGuard.exe"
#define MyServiceExeName "FileGuard.Service.exe"
#define MyServiceName "FileGuardGuard"
#define WinFspMsi "winfsp-2.1.25156.msi"

[Setup]
AppId={{BBF95E00-E30C-4E43-AE96-4D180C946DFE}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\FileGuard
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 需要管理员：装到 Program Files、写 HKLM、装服务都要
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=FileGuard-{#MyAppVersion}-setup
SetupIconFile=..\src\FileGuard.App\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; 不要在安装时因为单实例互斥量而报「程序正在运行」——
; 被守护服务拉起的实例杀不干净，会变成死锁。进程处理放在 PrepareToInstall。
DisableDirPage=auto

[Languages]
Name: "cn"; MessagesFile: "ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "installservice"; Description: "安装进程守护服务（SYSTEM 权限，普通用户无法结束程序或停止服务）"; GroupDescription: "进程守护："; Flags: unchecked

[Files]
Source: "..\publish\app-full\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 这个不能被 pack 进单文件 exe：WinFsp 靠它自身的磁盘路径找原生 winfsp-x64.dll
Source: "..\publish\app-full\winfsp-msil.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\service-full\{#MyServiceExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
; WinFsp 是 GPLv2：捆绑再分发必须附上许可证文本
Source: "redist\WinFsp-License.txt"; DestDir: "{app}"; DestName: "WinFsp-LICENSE.txt"; Flags: ignoreversion
Source: "redist\{#WinFspMsi}"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 装了守护服务的情况下，服务启动时会自己拉起主程序，这里不要再启动一次。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent; Check: not ServiceTaskSelected

[UninstallRun]
; 顺序不能反：先停服务，否则杀掉程序会立刻被拉起来。
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#MyAppExeName}"; Flags: runhidden waituntilterminated; RunOnceId: "KillApp"
Filename: "{app}\{#MyServiceExeName}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
var
  WinFspNeedsInstall: Boolean;

function ServiceTaskSelected: Boolean;
begin
  Result := WizardIsTaskSelected('installservice');
end;

function IsWinFspInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf32}\WinFsp\bin\winfsp-x64.dll'));
end;

/// 装新版之前先清场：停掉旧服务、结束旧进程。
/// 被旧服务守护时，直接 taskkill 会在两秒内被拉起来，所以必须先停服务。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  HadService: Boolean;
begin
  Result := '';
  HadService := False;

  // 全新安装到空目录：没有文件占用，别去动正在运行的实例。
  if not FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
    Exit;

  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#MyServiceName}') then
  begin
    HadService := True;
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;

  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#MyAppExeName}', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;

  if HadService then
  begin
    Exec(ExpandConstant('{app}\{#MyServiceExeName}'), '--uninstall', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // WinFsp：只在没装时装。失败不阻断安装——监视模式不依赖它。
  if WinFspNeedsInstall then
  begin
    if (not Exec('msiexec.exe',
          '/i "' + ExpandConstant('{tmp}\{#WinFspMsi}') + '" /qn /norestart',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode))
       or (ResultCode <> 0) then
      MsgBox('WinFsp 虚拟盘驱动安装失败（返回码 ' + IntToStr(ResultCode) + '）。' + #13#10#13#10 +
             '监视模式仍然可用；虚拟盘模式暂时不可用。' + #13#10 +
             '可以稍后从 https://winfsp.dev/rel/ 手动安装。',
             mbError, MB_OK);
  end;

  // 守护服务：放最后，它会自己把主程序拉起来。
  if ServiceTaskSelected then
  begin
    if (not Exec(ExpandConstant('{app}\{#MyServiceExeName}'),
          '--install "' + ExpandConstant('{app}\{#MyAppExeName}') + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode))
       or (ResultCode <> 0) then
      MsgBox('进程守护服务安装失败（返回码 ' + IntToStr(ResultCode) + '）。' + #13#10 +
             '主程序可以正常使用，稍后可在「设置 → 进程守护」里重试。',
             mbError, MB_OK);
  end;
end;

procedure InitializeWizard;
begin
  WinFspNeedsInstall := not IsWinFspInstalled;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('是否同时删除配置、隔离区和日志？' + #13#10#13#10 +
              '位置：C:\ProgramData\FileGuard' + #13#10 +
              '选择「否」会保留它们，重新安装后可以继续使用。',
              mbConfirmation, MB_YESNO) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\FileGuard'), True, True, True);
  end;
end;
