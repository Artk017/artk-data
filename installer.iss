; Artk Tools 安装包 —— 和 tubatools-master 同款（Inno Setup 6 modern 向导）
;
; 构建（在仓库根目录）：
;   "C:\Users\<你>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.2.3 installer.iss
; build-installer.ps1 会自动先发布到 dist\app，再调用 ISCC。
;
; 产物：dist\release\ArtkTools-win-Setup.exe
;
; 安装内容：dist\app 下的自包含发布文件（.NET / Windows App SDK 已打进去），
; 安装到 Program Files，自带卸载器、开始菜单 / 桌面快捷方式、完成页「运行 Artk Tools」。

#define MyAppName "Artk Tools"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Artk"
#define MyAppExeName "ArtkTools.exe"
#define MyAppCopyright "Copyright (C) 2026 Artk"

[Setup]
AppId={{A2E8E15C-4F5A-4F08-9F5B-5C1E9C0D2B71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright={#MyAppCopyright}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=dist\release
OutputBaseFilename=ArtkTools-win-Setup
SetupIconFile=Assets\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LanguageDetectionMethod=locale
ShowLanguageDialog=no
UpdateUninstallLogAppName=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
DisableDirPage=no
DirExistsWarning=no
AppendDefaultDirName=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Messages]
SetupAppTitle=安装 - {#MyAppName}
SetupWindowTitle=安装 - {#MyAppName}
WelcomeLabel2=此向导将引导您完成 [name/ver] 的安装过程。%n%n建议在继续之前关闭所有其他应用程序，以便安装程序更新相关的系统文件，无需重新启动计算机。
SelectDirBrowseLabel=如需安装到其他位置，请单击"浏览"选择目标文件夹。%n%n点击"安装"开始安装。
DiskSpaceWarning=至少需要 %1 KB 的可用空间才能安装，但所选驱动器只有 %2 KB 可用。%n%n是否仍要继续？
SelectStartMenuFolderBrowseLabel=如需选择其他文件夹，请单击"浏览"。%n%n点击"安装"开始安装。
ReadyLabel2a=单击"安装"开始安装，或单击"上一步"修改设置。
ReadyLabel2b=单击"安装"开始安装。
FinishedLabel=[name] 已成功安装到您的计算机中。
FinishedLabelNoIcons=[name] 已成功安装到您的计算机中。

ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&R)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)

SelectLanguageTitle=选择安装语言
SelectLanguageLabel=选择安装过程中使用的语言：

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; 旧版主程序叫 WinUIDemo.exe，升级时清掉，避免目录里留下两个 exe
[InstallDelete]
Type: files; Name: "{app}\WinUIDemo.exe"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 主程序清单要求管理员权限（读 SATA SMART 需要），Inno 6 默认用「原用户权限」启动会报 740，
; runascurrentuser 让完成页直接用安装器已提升的权限启动，不再二次弹 UAC。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
const
  TileBackupKey = 'Software\Microsoft\Windows\CurrentVersion\AppListBackup';
  { 旧版（Velopack 安装）在「应用和功能」里的卸载键 }
  LegacyUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\NovaWorkbench';

var
  DeleteUserData: Boolean;
  LegacyDir: String;

function IsWindowsVersionOk: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  // Windows 10 2004/20H1 = Build 19041
  Result := (Version.Major > 10) or
            ((Version.Major = 10) and (Version.Build >= 19041));
end;

function NormalizeDir(const S: String): String;
begin
  Result := Trim(S);
  while (Length(Result) > 3) and
        ((Result[Length(Result)] = '\') or (Result[Length(Result)] = '/')) do
    Delete(Result, Length(Result), 1);
end;

{ 从 "G:\dow\6666\Update.exe" --uninstall 里取目录 }
function DirFromCommand(const Command: String): String;
var
  S: String;
  P: Integer;
begin
  S := Trim(Command);
  if (Length(S) > 0) and (S[1] = '"') then
  begin
    Delete(S, 1, 1);
    P := Pos('"', S);
    if P > 0 then
      S := Copy(S, 1, P - 1);
  end
  else
  begin
    P := Pos(' --', S);
    if P > 0 then
      S := Copy(S, 1, P - 1);
  end;
  Result := ExtractFileDir(Trim(S));
end;

{ 检测旧版 Velopack 安装目录；返回 True 表示找到 }
function FindLegacyInstall: Boolean;
var
  D, Cmd: String;
begin
  LegacyDir := '';
  Result := False;

  if RegQueryStringValue(HKCU, LegacyUninstallKey, 'InstallLocation', D) or
     RegQueryStringValue(HKLM, LegacyUninstallKey, 'InstallLocation', D) then
    LegacyDir := D;

  if Trim(LegacyDir) = '' then
  begin
    if (RegQueryStringValue(HKCU, LegacyUninstallKey, 'UninstallString', Cmd) or
        RegQueryStringValue(HKLM, LegacyUninstallKey, 'UninstallString', Cmd)) then
      LegacyDir := DirFromCommand(Cmd);
  end;

  LegacyDir := NormalizeDir(LegacyDir);
  Result := (LegacyDir <> '') and DirExists(LegacyDir);
end;

{ 清掉旧版 Velopack 卸载项（HKCU/HKLM），reg.exe 删除最稳，忽略不存在的键 }
procedure DeleteLegacyRegistry;
var
  ResultCode: Integer;
begin
  Exec('reg.exe', 'delete "HKCU\' + LegacyUninstallKey + '" /f', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('reg.exe', 'delete "HKLM\' + LegacyUninstallKey + '" /f', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ 安装完成后移除旧版本：只删旧程序目录和旧卸载键，保留 %LOCALAPPDATA%\Nova 用户数据。
  注意：不能调用旧的 Update.exe --uninstall，它会触发 UninstallCleanup.PurgeAll() 删除用户数据。 }
procedure CleanupLegacyInstall;
var
  AppDir, Dir: String;
begin
  Dir := '';
  if FindLegacyInstall then
    Dir := LegacyDir;

  DeleteLegacyRegistry;

  if Dir = '' then
    Exit;

  AppDir := NormalizeDir(ExpandConstant('{app}'));
  if LowerCase(Dir) <> LowerCase(AppDir) then
    DelTree(Dir, True, True, True);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Msg: String;
begin
  if not IsWindowsVersionOk then
  begin
    Msg := '本程序需要 Windows 10 2004 (Build 19041) 或更高版本。' + #13#10 +
           '您当前的系统版本过低，无法运行本程序。' + #13#10#13#10 +
           '请先更新 Windows 系统后再安装。';
    MsgBox(Msg, mbCriticalError, MB_OK);
    Result := '系统版本不满足要求，安装已取消。';
    Exit;
  end;

  { 检测到旧版：安装完成后自动移除旧程序目录（保留用户数据） }
  if FindLegacyInstall then
  begin
    if not WizardSilent then
      MsgBox('检测到旧版 Artk Tools。' + #13#10#13#10 +
             '旧版位置：' + LegacyDir + #13#10 +
             '安装完成后会自动移除旧版本的程序文件，并清理旧版卸载项。' + #13#10 +
             '您的设置与缓存数据（%LOCALAPPDATA%\Nova）会保留。',
             mbInformation, MB_OK);
  end;

  Result := '';
end;

{ 文件复制完成后清理旧版（此时新版本已就位，中途失败也不会丢旧版） }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    CleanupLegacyInstall;
end;

// 和程序内 UninstallCleanup 一致：清掉开始菜单磁贴备份里残留的 NovaWorkbench 项
procedure RemoveTileBackups;
var
  Names: TArrayOfString;
  I: Integer;
  Data: String;
begin
  if RegGetValueNames(HKCU, TileBackupKey, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Data := '';
      RegQueryStringValue(HKCU, TileBackupKey, Names[I], Data);
      if (Pos('NovaWorkbench', Names[I]) > 0) or (Pos('NovaWorkbench', Data) > 0) then
        RegDeleteValue(HKCU, TileBackupKey, Names[I]);
    end;
  end;
end;

// === 卸载：询问是否删除用户数据（默认保留；静默卸载自动取默认"否"） ===
procedure AskDeleteUserData;
var
  DataDir: String;
begin
  DeleteUserData := False;
  DataDir := ExpandConstant('{localappdata}\Nova');

  if DirExists(DataDir) then
  begin
    if SuppressibleMsgBox('是否同时删除 Artk Tools 的用户数据？' + #13#10 + #13#10 +
              '删除内容包括：' + #13#10 +
              '· 设置与配置' + #13#10 +
              '· 清单缓存与下载记录' + #13#10 +
              '· 图标缓存' + #13#10 + #13#10 +
              '数据位于 ' + DataDir + '。' + #13#10 +
              '此操作不可恢复。' + #13#10 + #13#10 +
              '选择"是"删除用户数据，选择"否"仅卸载程序、保留数据。',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DeleteUserData := True;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    AskDeleteUserData
  else if CurUninstallStep = usPostUninstall then
  begin
    RemoveTileBackups;

    if DeleteUserData then
      DelTree(ExpandConstant('{localappdata}\Nova'), True, True, True);
  end;
end;
