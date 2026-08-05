[Code]
const
  DeskBoxAdminCleanupParam = '/ADMINCLEANUP=';
  DeskBoxAppCompatLayersKey = 'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers';

var
  IsMigrationAdminCleanupMode: Boolean;
  MigrationAdminCleanupPath: string;

function PrepareDeskBoxDependencies(var NeedsRestart: Boolean): string; forward;

procedure ExitProcess(ExitCode: Integer);
  external 'ExitProcess@kernel32.dll stdcall';

function TryReadLegacyInstallPathFromRegistry(var InstallPath: string): Boolean;
begin
  Result := False;
  InstallPath := '';

  if RegQueryStringValue(HKEY_CURRENT_USER, DeskBoxLegacyUninstallKey, 'InstallLocation', InstallPath) and
     IsLegacyInstallPath(InstallPath) then
  begin
    Result := True;
    Exit;
  end;

  InstallPath := '';
  if RegQueryStringValue(HKEY_CURRENT_USER, DeskBoxLegacyWowUninstallKey, 'InstallLocation', InstallPath) and
     IsLegacyInstallPath(InstallPath) then
  begin
    Result := True;
    Exit;
  end;

  InstallPath := '';
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, DeskBoxLegacyUninstallKey, 'InstallLocation', InstallPath) and
     IsLegacyInstallPath(InstallPath) then
  begin
    Result := True;
    Exit;
  end;

  InstallPath := '';
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, DeskBoxLegacyWowUninstallKey, 'InstallLocation', InstallPath) and
     IsLegacyInstallPath(InstallPath) then
  begin
    Result := True;
    Exit;
  end;
end;

function TryDetectLegacyInstallPath(var InstallPath: string): Boolean;
var
  CandidatePath: string;
begin
  Result := False;
  InstallPath := '';

  if TryReadLegacyInstallPathFromRegistry(InstallPath) then
  begin
    Result := True;
    Exit;
  end;

  CandidatePath := ExpandConstant('{pf}\DeskBox');
  if IsLegacyInstallPath(CandidatePath) then
  begin
    InstallPath := CandidatePath;
    Result := True;
    Exit;
  end;

  CandidatePath := ExpandConstant('{pf32}\DeskBox');
  if IsLegacyInstallPath(CandidatePath) then
  begin
    InstallPath := CandidatePath;
    Result := True;
    Exit;
  end;
end;

function TryReadAdminCleanupMode: Boolean;
var
  I: Integer;
  Param: string;
begin
  Result := False;
  MigrationAdminCleanupPath := '';

  for I := 1 to ParamCount do
  begin
    Param := ParamStr(I);
    if CompareText(Copy(Param, 1, Length(DeskBoxAdminCleanupParam)), DeskBoxAdminCleanupParam) = 0 then
    begin
      MigrationAdminCleanupPath := Copy(Param, Length(DeskBoxAdminCleanupParam) + 1, MaxInt);
      Result := IsLegacyInstallPath(MigrationAdminCleanupPath);
      Exit;
    end;
  end;
end;

procedure StopLegacyDeskBoxProcess;
begin
  if not StopDeskBoxProcessesAtPath(MigrationAdminCleanupPath) then
    Log('DeskBox migration could not stop only the legacy install processes.');
end;

procedure DeleteShortcutIfExists(Path: string);
begin
  if FileExists(Path) then
  begin
    if DeleteFile(Path) then
      Log('DeskBox migration deleted shortcut: ' + Path)
    else
      Log('DeskBox migration failed to delete shortcut: ' + Path);
  end;
end;

procedure DeleteShortcutIfTargetsInstall(Path: string; InstallPath: string);
begin
  if ShortcutTargetsInstall(Path, InstallPath) then
    DeleteShortcutIfExists(Path);
end;

procedure DeleteAppCompatLayerValue(RootKey: Integer; ExePath: string);
var
  Value: string;
begin
  if ExePath = '' then
    Exit;

  if RegQueryStringValue(RootKey, DeskBoxAppCompatLayersKey, ExePath, Value) then
  begin
    if Pos('RUNASADMIN', Uppercase(Value)) > 0 then
    begin
      if RegDeleteValue(RootKey, DeskBoxAppCompatLayersKey, ExePath) then
        Log('DeskBox migration removed AppCompat RUNASADMIN value: ' + ExePath)
      else
        Log('DeskBox migration failed to remove AppCompat value: ' + ExePath);
    end;
  end;
end;

procedure CleanupCurrentUserAppCompatFlags(LegacyInstallPath: string);
begin
  if LegacyInstallPath <> '' then
  begin
    DeleteAppCompatLayerValue(
      HKEY_CURRENT_USER,
      AddBackslash(LegacyInstallPath) + DeskBoxLegacyExeName);
  end;

  DeleteAppCompatLayerValue(
    HKEY_CURRENT_USER,
    ExpandConstant('{localappdata}\Programs\DeskBox\DeskBox.exe'));
end;

function PerformMigrationAdminCleanup(LegacyInstallPath: string): Boolean;
var
  LegacyExePath: string;
begin
  Result := False;

  if not IsLegacyInstallPath(LegacyInstallPath) then
  begin
    Log('DeskBox migration rejected cleanup path: ' + LegacyInstallPath);
    Exit;
  end;

  LegacyExePath := AddBackslash(LegacyInstallPath) + DeskBoxLegacyExeName;
  StopLegacyDeskBoxProcess;

  DeleteShortcutIfTargetsInstall(ExpandConstant('{commonprograms}\DeskBox.lnk'), LegacyInstallPath);
  DeleteShortcutIfTargetsInstall(ExpandConstant('{commondesktop}\DeskBox.lnk'), LegacyInstallPath);
  DeleteShortcutIfTargetsInstall(ExpandConstant('{commonstartup}\DeskBox.lnk'), LegacyInstallPath);
  DeleteShortcutIfTargetsInstall(ExpandConstant('{commonappdata}\Microsoft\Windows\Start Menu\Programs\DeskBox.lnk'), LegacyInstallPath);
  DeleteShortcutIfTargetsInstall(ExpandConstant('{commonappdata}\Microsoft\Windows\Start Menu\Programs\Startup\DeskBox.lnk'), LegacyInstallPath);
  DeleteShortcutIfTargetsInstall(ExpandConstant('{userprograms}\DeskBox.lnk'), LegacyInstallPath);
  DeleteShortcutIfTargetsInstall(ExpandConstant('{userdesktop}\DeskBox.lnk'), LegacyInstallPath);
  DeleteShortcutIfTargetsInstall(ExpandConstant('{userstartup}\DeskBox.lnk'), LegacyInstallPath);
  DeleteShortcutIfTargetsInstall(ExpandConstant('{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\DeskBox.lnk'), LegacyInstallPath);

  DeleteAppCompatLayerValue(HKEY_LOCAL_MACHINE, LegacyExePath);

  if RegKeyExists(HKEY_LOCAL_MACHINE, DeskBoxLegacyUninstallKey) then
    RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, DeskBoxLegacyUninstallKey);

  if RegKeyExists(HKEY_LOCAL_MACHINE, DeskBoxLegacyWowUninstallKey) then
    RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, DeskBoxLegacyWowUninstallKey);

  if DirExists(LegacyInstallPath) then
  begin
    if not DelTree(LegacyInstallPath, True, True, True) then
    begin
      Log('DeskBox migration failed to remove legacy directory: ' + LegacyInstallPath);
      Log('DeskBox migration will continue because user-scope install can still proceed.');
    end;
  end;

  Result := True;
end;

function RunMigrationAdminCleanup(LegacyInstallPath: string): Boolean;
var
  ResultCode: Integer;
  Parameters: string;
begin
  Parameters :=
    '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART "' +
    DeskBoxAdminCleanupParam + LegacyInstallPath + '"';

  Log('DeskBox migration launching admin cleanup for: ' + LegacyInstallPath);
  if not ShellExec(
      'runas',
      ExpandConstant('{srcexe}'),
      Parameters,
      '',
      SW_SHOW,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    Log('DeskBox migration admin cleanup could not be launched.');
    Result := False;
    Exit;
  end;

  Log('DeskBox migration admin cleanup exit code: ' + IntToStr(ResultCode));
  Result := ResultCode = 0;
end;

function InitializeSetup: Boolean;
var
  LegacyInstallPath: string;
begin
  IsMigrationAdminCleanupMode := TryReadAdminCleanupMode;

  if IsMigrationAdminCleanupMode then
  begin
    if PerformMigrationAdminCleanup(MigrationAdminCleanupPath) then
      ExitProcess(0)
    else
      ExitProcess(1);

    Result := False;
    Exit;
  end;

  Result := True;
  if TryDetectLegacyInstallPath(LegacyInstallPath) then
  begin
    Log('DeskBox migration detected legacy install: ' + LegacyInstallPath);
    CleanupCurrentUserAppCompatFlags(LegacyInstallPath);

    if not RunMigrationAdminCleanup(LegacyInstallPath) then
      Log('DeskBox migration admin cleanup failed; continuing with current-user install.');

    CleanupCurrentUserAppCompatFlags(LegacyInstallPath);
  end
  else
  begin
    CleanupCurrentUserAppCompatFlags('');
  end;

  if not PrepareDirectInstallPlan then
  begin
    Result := False;
    Exit;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := IsMigrationAdminCleanupMode or
    (DirectInstallUpgrade and (PageID = wpSelectDir));
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  DependencyError: string;
begin
  Result := '';

  // Close only the DeskBox process that belongs to the install being updated.
  // Restart Manager remains the final file-lock fallback for Setup itself.
  if not StopDeskBoxProcessesAtPath(WizardDirValue) then
    Log('DeskBox path-scoped process shutdown failed; Setup will continue with Restart Manager handling file locks.');

  // Give the process time to fully exit before Restart Manager runs.
  Sleep(2000);

  Log('DeskBox process termination completed.');

  if IsMigrationAdminCleanupMode then
    Exit;

  DependencyError := PrepareDeskBoxDependencies(NeedsRestart);
  if DependencyError <> '' then
    Result := DependencyError;
end;
