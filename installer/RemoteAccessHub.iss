; RemoteAccessHub 설치 파일 스크립트 (Inno Setup 6)
; build.ps1 -Publish -Installer 로 만듭니다. 버전은 /DAppVersion 으로 받습니다.

#define AppName "RemoteAccessHub"
#define AppExe "RemoteAccessHub.exe"
#define AppPublisher "jaehun6912"
#define AppUrl "https://github.com/jaehun6912/RemoteAccessHub"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{1606DA94-30DA-4BEC-A124-12B174BA98D1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=RemoteAccessHub-setup-{#AppVersion}
SetupIconFile=..\src\RemoteAccessHub\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 관리자 권한 없이 사용자 폴더에 설치한다(서명이 없어 UAC 창을 띄우지 않는 편이 낫다).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로 가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} 실행"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\docs"

[Code]
function DotNetDesktop8Installed(): Boolean;
var
  Base: String;
  Found: TFindRec;
begin
  Result := False;
  Base := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  if not DirExists(Base) then
    Exit;
  if not FindFirst(Base + '\*', Found) then
    Exit;
  try
    repeat
      if ((Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Copy(Found.Name, 1, 2) = '8.') then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
  if Result then
    Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function InitializeSetup(): Boolean;
var
  Missing: String;
begin
  Missing := '';
  if not DotNetDesktop8Installed() then
    Missing := Missing + '  · .NET 8 Desktop Runtime (x64)' + #13#10;
  if not WebView2Installed() then
    Missing := Missing + '  · Microsoft Edge WebView2 Runtime' + #13#10;

  Result := True;
  if Missing <> '' then
    Result := MsgBox(
      '이 프로그램을 실행하려면 아래가 필요합니다.' + #13#10#13#10 + Missing + #13#10 +
      '지금 설치를 계속할 수는 있지만, 위 항목을 설치하기 전에는 프로그램이 실행되지 않습니다.' + #13#10 +
      '자세한 설치 방법은 docs\사용안내.md 의 준비물을 보세요.' + #13#10#13#10 +
      '계속할까요?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings: String;
  Logs: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  // 조용히 제거할 때는 묻지 않고 사용자 파일을 그대로 둔다.
  if UninstallSilent then
    Exit;
  Settings := ExpandConstant('{userappdata}\RemoteAccessHub');
  Logs := ExpandConstant('{localappdata}\RemoteAccessHub');
  if not (DirExists(Settings) or DirExists(Logs)) then
    Exit;
  if MsgBox('설정과 기록 파일도 지울까요?' + #13#10#13#10 +
            '지우지 않으면 다시 설치했을 때 그대로 쓸 수 있습니다.' + #13#10 +
            '(Windows VPN 연결은 어느 쪽이든 그대로 둡니다.)',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    DelTree(Settings, True, True, True);
    DelTree(Logs, True, True, True);
  end;
end;
