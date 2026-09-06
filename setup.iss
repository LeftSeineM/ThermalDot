#define ProductVersion "1.4.0"
#ifndef PackageRoot
  #define PackageRoot SourcePath
#endif

[Setup]
AppId={{CD90EC63-9CA6-4725-B63D-EBD676C40271}
AppName=温度球
AppVersion={#ProductVersion}
AppPublisher=ThermalDot
DefaultDirName={localappdata}\Programs\ThermalDot
DefaultGroupName=温度球
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir={#PackageRoot}\release
OutputBaseFilename=温度球-{#ProductVersion}-安装版
SetupIconFile={#PackageRoot}\src\ThermalDot.ico
UninstallDisplayIcon={app}\ThermalDot.exe
UninstallDisplayName=温度球 {#ProductVersion}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
InfoBeforeFile={#PackageRoot}\docs\安装须知.txt
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "{#PackageRoot}\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PackageRoot}\publish\ThermalDot.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageRoot}\docs\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageRoot}\docs\Notices.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageRoot}\components\PawnIO_setup.exe"; DestDir: "{app}\components"; Flags: ignoreversion

[Icons]
Name: "{group}\温度球"; Filename: "{app}\ThermalDot.exe"; WorkingDir: "{app}"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{userdesktop}\温度球"; Filename: "{app}\ThermalDot.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\components\PawnIO_setup.exe"; Description: "打开官方 PawnIO 安装向导（CPU 温度读取，需要管理员权限）"; Flags: shellexec postinstall waituntilterminated skipifsilent; Check: NeedsPawnIO
Filename: "{app}\ThermalDot.exe"; Description: "启动温度球（将申请管理员权限）"; Flags: shellexec postinstall nowait skipifsilent

[Code]
function NeedsPawnIO: Boolean;
var InstalledVersion: String;
begin
  Result := not RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayVersion', InstalledVersion);
end;
