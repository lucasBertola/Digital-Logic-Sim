@echo off
REM Lance NOTRE app (ce fork). Recompile d'abord si un fichier .cs a change depuis le dernier
REM build, sinon lance directement le dernier build.
cd /d "%~dp0"
set "EXE=%~dp0Builds\Windows\DigitalLogicSim.exe"
set "MARKER=%~dp0Builds\Windows\.lastbuild"
set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.0.46f1\Editor\Unity.exe"

REM --- Faut-il recompiler ? (exe/marqueur absent, ou un .cs plus recent que le dernier build) ---
powershell -NoProfile -ExecutionPolicy Bypass -Command "if(-not(Test-Path '%EXE%')){exit 1}; if(-not(Test-Path '%MARKER%')){exit 1}; $mt=(Get-Item '%MARKER%').LastWriteTimeUtc; $n=(Get-ChildItem '%~dp0Assets' -Recurse -Filter *.cs -ErrorAction SilentlyContinue | Measure-Object LastWriteTimeUtc -Maximum).Maximum; if($n -and $n -gt $mt){exit 1}else{exit 0}"

if errorlevel 1 (
  echo Code modifie depuis le dernier build ^(ou aucun build^) : compilation...
  if not exist "%UNITY%" ( echo Editeur Unity introuvable : %UNITY% & pause & exit /b 1 )
  taskkill /IM DigitalLogicSim.exe /F >nul 2>&1
  powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%UNITY%' -quit -batchmode -projectPath '%~dp0.' -executeMethod BuildTools.BuildWindows -buildOutput '%~dp0Builds\Windows' -logFile '%TEMP%\dls_build.log'; exit $LASTEXITCODE"
  if errorlevel 1 ( echo. & echo ECHEC de la compilation. Log : %TEMP%\dls_build.log & pause & exit /b 1 )
  echo built> "%MARKER%"
  echo Compilation OK.
) else (
  echo Build a jour : lancement direct.
)

REM --- Injecte la cle API persistee (Machine/User) dans l'environnement du process lance ---
for /f "usebackq delims=" %%K in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$k=[Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','Machine'); if(-not $k){$k=[Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','User')}; if(-not $k){$k=$env:ANTHROPIC_API_KEY}; Write-Output $k"`) do set "ANTHROPIC_API_KEY=%%K"

start "" "%EXE%"
