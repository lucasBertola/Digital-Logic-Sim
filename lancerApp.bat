@echo off
REM Lance l'app (ce fork).
REM  - Build absent            : telecharge la derniere Release GitHub (aucun besoin d'Unity).
REM  - Code plus recent que le build et Unity installe : recompile (mode interactif, voir CLAUDE.md).
REM  - Code plus recent, sans Unity : re-telecharge la derniere Release.
REM  - Sinon                   : lance directement le dernier build.
setlocal
cd /d "%~dp0"
set "EXE=%~dp0Builds\Windows\DigitalLogicSim.exe"
set "MARKER=%~dp0Builds\Windows\.lastbuild"
set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.0.46f1\Editor\Unity.exe"
set "REPO=lucasBertola/Digital-Logic-Sim"

if not exist "%EXE%" goto download

REM --- Le code a-t-il change depuis le dernier build ? (un .cs plus recent que le marqueur) ---
powershell -NoProfile -ExecutionPolicy Bypass -Command "if(-not(Test-Path '%MARKER%')){exit 1}; $mt=(Get-Item '%MARKER%').LastWriteTimeUtc; $n=(Get-ChildItem '%~dp0Assets' -Recurse -Filter *.cs -ErrorAction SilentlyContinue | Measure-Object LastWriteTimeUtc -Maximum).Maximum; if($n -and $n -gt $mt){exit 1}else{exit 0}"
if not errorlevel 1 goto launch

if exist "%UNITY%" goto build
echo Code plus recent que le build et Unity absent : telechargement de la derniere Release...
goto download

:build
echo Code modifie depuis le dernier build : compilation avec Unity (la fenetre de l'editeur s'ouvre quelques secondes)...
taskkill /IM DigitalLogicSim.exe /F >nul 2>&1
"%UNITY%" -projectPath "%~dp0." -executeMethod BuildTools.BuildWindows -buildOutput "%~dp0Builds\Windows" -quit -logFile "%TEMP%\dls_build.log"
findstr /C:"BUILD SUCCEEDED" "%TEMP%\dls_build.log" >nul
if errorlevel 1 ( echo. & echo ECHEC de la compilation. Log : %TEMP%\dls_build.log & pause & exit /b 1 )
echo built> "%MARKER%"
echo Compilation OK.
goto launch

:download
echo Telechargement de la derniere version publiee (github.com/%REPO%/releases)...
taskkill /IM DigitalLogicSim.exe /F >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $h=@{'User-Agent'='DigitalLogicSim'}; try { $r=Invoke-RestMethod 'https://api.github.com/repos/%REPO%/releases/latest' -Headers $h } catch { Write-Host 'Aucune Release publiee sur GitHub (ou pas de connexion).'; exit 2 }; $a=$r.assets | Where-Object { $_.name -like '*Windows*.zip' } | Select-Object -First 1; if(-not $a){ $a=$r.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1 }; if(-not $a){ Write-Host 'La Release ne contient pas de zip.'; exit 2 }; Write-Host ('Version ' + $r.tag_name + ' : ' + $a.name + ' (' + [math]::Round($a.size/1MB) + ' Mo)'); $zip=Join-Path $env:TEMP 'DigitalLogicSim-Windows.zip'; Invoke-WebRequest $a.browser_download_url -OutFile $zip -Headers $h; New-Item -ItemType Directory -Force '%~dp0Builds\Windows' | Out-Null; Expand-Archive -Path $zip -DestinationPath '%~dp0Builds\Windows' -Force; Remove-Item $zip; exit 0"
if errorlevel 1 ( echo. & echo Impossible de recuperer l'application. & pause & exit /b 1 )
if not exist "%EXE%" ( echo Le zip telecharge ne contient pas DigitalLogicSim.exe. & pause & exit /b 1 )
echo downloaded> "%MARKER%"
echo Telechargement OK.

:launch
REM --- Cle API (Ask Claude) : variable Machine, sinon User, sinon session courante ---
for /f "usebackq delims=" %%K in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$k=[Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','Machine'); if(-not $k){$k=[Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','User')}; if(-not $k){$k=$env:ANTHROPIC_API_KEY}; Write-Output $k"`) do set "ANTHROPIC_API_KEY=%%K"
if not defined ANTHROPIC_API_KEY echo (ANTHROPIC_API_KEY absente : l'assistant Ask Claude sera indisponible, le reste fonctionne.)
start "" "%EXE%"
endlocal
