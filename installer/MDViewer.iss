; Установщик MD Viewer (Inno Setup 6). Ставится в папку пользователя без прав администратора.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6B0F8E7C-3D52-4C31-9E7A-8A4B1F2D6C01}
AppName=MD Viewer
AppVersion={#AppVersion}
AppVerName=MD Viewer {#AppVersion}
AppPublisher=madhare74
AppPublisherURL=https://github.com/madhare74/md-viewer
DefaultDirName={localappdata}\Programs\MD Viewer
DefaultGroupName=MD Viewer
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=MDViewer-Setup
SetupIconFile=..\src\MdViewer\Assets\app.ico
UninstallDisplayIcon={app}\MDViewer.exe
UninstallDisplayName=MD Viewer
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "assoc"; Description: "Открывать файлы .md и .markdown в MD Viewer"; GroupDescription: "Связь с файлами:"
Name: "desktopicon"; Description: "Значок на рабочем столе"; GroupDescription: "Значки:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MD Viewer"; Filename: "{app}\MDViewer.exe"
Name: "{autodesktop}\MD Viewer"; Filename: "{app}\MDViewer.exe"; Tasks: desktopicon

[Registry]
; Пункт «Открыть с помощью → MD Viewer» — всегда
Root: HKCU; Subkey: "Software\Classes\Applications\MDViewer.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "MD Viewer"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\MDViewer.exe\shell\open\command"; ValueType: string; ValueData: """{app}\MDViewer.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\MDViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\MDViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""
; Тип файла MD Viewer
Root: HKCU; Subkey: "Software\Classes\MDViewer.Markdown"; ValueType: string; ValueData: "Документ Markdown"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\MDViewer.Markdown\DefaultIcon"; ValueType: string; ValueData: """{app}\MDViewer.exe"",0"
Root: HKCU; Subkey: "Software\Classes\MDViewer.Markdown\shell\open\command"; ValueType: string; ValueData: """{app}\MDViewer.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "MDViewer.Markdown"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "MDViewer.Markdown"; ValueData: ""; Flags: uninsdeletevalue
; Связь по умолчанию — по выбору
Root: HKCU; Subkey: "Software\Classes\.md"; ValueType: string; ValueData: "MDViewer.Markdown"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCU; Subkey: "Software\Classes\.markdown"; ValueType: string; ValueData: "MDViewer.Markdown"; Flags: uninsdeletevalue; Tasks: assoc

[Run]
Filename: "{app}\MDViewer.exe"; Description: "Запустить MD Viewer"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\MD Viewer\WebView2"
