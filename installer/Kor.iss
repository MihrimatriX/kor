; Kor kurulum betiği (Inno Setup 6.7+)
; Derleme:  ISCC.exe /DAppVersion=0.3.0 /DSourceExe=..\artifacts\portable\Kor.exe installer\Kor.iss
; CI'da .github/workflows/release.yml bu betiği çalıştırır.

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\artifacts\portable\Kor.exe"
#endif

#define AppName "Kor"
#define AppPublisher "side project GAMES"
#define AppUrl "https://mihrimatrix.github.io/kor/"
#define RepoUrl "https://github.com/MihrimatriX/kor"

[Setup]
; AppId sabit kalmalı: sonraki sürümler aynı kurulumun üstüne yazar (güncelleme gibi davranır)
AppId={{6E1C2B8F-4D3A-4F6B-9C2E-7A5D1B0E9F42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=© 2026 {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} kurulumu
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; Kor sensörler için zaten yönetici izniyle çalışıyor; Program Files'a kurulum da yönetici ister
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Sistem dili Türkçe/İngilizce ise dil sorusu çıkmaz
ShowLanguageDialog=auto
OutputDir=..\artifacts
OutputBaseFilename=Kor-Setup-{#AppVersion}
SetupIconFile=..\Assets\kor.ico
UninstallDisplayIcon={app}\Kor.exe
UninstallDisplayName={#AppName}
; Windows'un açık/koyu temasını izler, Windows 11 görünümü
WizardStyle=modern dynamic windows11
WizardImageFile=wizard1x.bmp,wizard2x.bmp
WizardImageFileDynamicDark=wizard1x.bmp,wizard2x.bmp
WizardSmallImageFile=small1x.bmp,small2x.bmp
; Küçük logo saydam (32 bit BMP), açık ve koyu temada aynı dosya
WizardImageAlphaFormat=defined
Compression=lzma2/max
SolidCompression=yes
; Açık bir Kor varsa Restart Manager yerine kendi "--exit" komutumuzu kullanıyoruz (fanları BIOS'a geri verir)
CloseApplications=no
RestartApplications=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.PawnIoTask=PawnIO sürücüsünü kur (CPU sıcaklığı ve anakart fanları için; winget ile)
english.PawnIoTask=Install the PawnIO driver (needed for CPU temperature and motherboard fans; via winget)
turkish.PawnIoStatus=PawnIO sürücüsü kuruluyor…
english.PawnIoStatus=Installing the PawnIO driver…
turkish.Extras=Ek seçenekler:
english.Extras=Extras:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "pawnio"; Description: "{cm:PawnIoTask}"; GroupDescription: "{cm:Extras}"; Check: not IsPawnIoInstalled

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "Kor.exe"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Kor.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Kor.exe"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/c winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements"; \
  StatusMsg: "{cm:PawnIoStatus}"; Flags: runhidden waituntilterminated; Tasks: pawnio
; Kurulum zaten yönetici olarak çalıştığı için Kor'u da öyle başlat (ikinci UAC sorusu çıkmasın)
Filename: "{app}\Kor.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; "Windows ile başlat" görevini kaldır
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Kor Hardware Monitor"" /F"; Flags: runhidden; RunOnceId: "RemoveStartupTask"

[UninstallDelete]
; Ayarlar ve geçmiş (%AppData%\Kor) bilerek silinmez; yeniden kurulumda geri gelir.
Type: dirifempty; Name: "{app}"

[Code]
function IsPawnIoInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO')
         or RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO');
end;

{ Çalışan Kor'a "düzgün kapan" sinyali gönderir: fan kontrolü BIOS'a döner, ayarlar kaydedilir. }
procedure StopRunningKor(const ExePath: String);
var
  Code: Integer;
begin
  if FileExists(ExePath) then
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningKor(ExpandConstant('{app}\Kor.exe'));
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopRunningKor(ExpandConstant('{app}\Kor.exe'));
  Result := True;
end;
