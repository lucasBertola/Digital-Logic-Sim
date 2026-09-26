@echo off
REM Publie le build courant (Builds\Windows) comme Release GitHub, pour que lancerApp.bat puisse le
REM telecharger sur une machine sans Unity. Tout se fait en console, sans clic :
REM   version = derniere Release + 1 (V1 s'il n'y en a aucune), notes redigees par Claude a partir des commits
REM   depuis la derniere Release (liste brute des commits si pas de cle API),
REM   push des commits, creation de la Release avec le zip en piece jointe.
REM Usage : publierRelease.bat [version]      ex: publierRelease.bat V3   (sans argument : version proposee, Entree = accepter)
REM Requiert GitHub CLI (gh) connecte une fois avec :  gh auth login
REM Sans gh : le zip et les notes sont prepares et la page de creation de Release s'ouvre pre-remplie.
setlocal EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"
set "SRC=%~dp0Builds\Windows"
set "ZIP=%~dp0Builds\DigitalLogicSim-Windows.zip"
set "NOTES=%~dp0Builds\release-notes.md"
set "REPO=lucasBertola/Digital-Logic-Sim"

if not exist "%SRC%\DigitalLogicSim.exe" ( echo Aucun build dans Builds\Windows : compile d'abord ^(Tools ^> Build Windows Player^). & pause & exit /b 1 )

REM --- gh : dans le PATH, sinon aux emplacements d'installation habituels ---
set "GH="
where gh >nul 2>&1 && set "GH=gh"
if not defined GH if exist "%LOCALAPPDATA%\Programs\gh\bin\gh.exe" set "GH=%LOCALAPPDATA%\Programs\gh\bin\gh.exe"
if not defined GH if exist "%ProgramFiles%\GitHub CLI\gh.exe" set "GH=%ProgramFiles%\GitHub CLI\gh.exe"

REM --- Derniere Release publiee -> version proposee (+1) ---
set "LAST="
set "NEXT="
for /f "usebackq tokens=1,2 delims=|" %%V in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $h=@{'User-Agent'='DigitalLogicSim'}; try { $r=Invoke-RestMethod 'https://api.github.com/repos/%REPO%/releases/latest' -Headers $h; $t=[string]$r.tag_name } catch { $t='' }; $n=0; if($t -match '(\d+)'){ $n=[int]$matches[1] }; Write-Output ('V' + ($n+1) + '|' + $t)"`) do ( set "NEXT=%%V" & set "LAST=%%W" )
if not defined NEXT set "NEXT=V1"
set "TAG=%~1"
if "%TAG%"=="" set /p "TAG=Version a publier [%NEXT%] : "
if "%TAG%"=="" set "TAG=%NEXT%"

REM --- Notes : un point par commit depuis la derniere Release (ou depuis le debut du fork) ---
if defined LAST (
  set "RANGE=%LAST%..HEAD"
  set "TITLE=Changements depuis %LAST%"
) else (
  set "RANGE=HEAD"
  git rev-parse --verify -q upstream/main >nul 2>&1 && set "RANGE=upstream/main..HEAD"
  set "TITLE=Premiere version"
)
> "%NOTES%" echo ## !TITLE!
>> "%NOTES%" echo.
git log !RANGE! --no-merges --pretty=format:"- %%s" >> "%NOTES%"
>> "%NOTES%" echo.
set "COMMITS=%~dp0Builds\release-commits.txt"
git log !RANGE! --no-merges --pretty=format:"* %%s%%n%%b" > "%COMMITS%"

REM --- Notes redigees par Claude (API Anthropic) quand une cle est disponible ; sinon la liste brute reste ---
for /f "usebackq delims=" %%K in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$k=[Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','Machine'); if(-not $k){$k=[Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','User')}; if(-not $k){$k=$env:ANTHROPIC_API_KEY}; Write-Output $k"`) do set "ANTHROPIC_API_KEY=%%K"
if defined ANTHROPIC_API_KEY (
  echo Redaction des notes de version par Claude...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\releaseNotes.ps1" -CommitsFile "%COMMITS%" -OutFile "%NOTES%" -Title "!TITLE!" -Tag "%TAG%"
  if errorlevel 1 echo ^(echec : la liste brute des commits est conservee^)
) else (
  echo ^(ANTHROPIC_API_KEY absente : notes = liste brute des commits^)
)
echo.
echo ==== %TAG% ====
type "%NOTES%"
echo.

echo Creation du zip...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; if(Test-Path '%ZIP%'){Remove-Item '%ZIP%'}; Compress-Archive -Path '%SRC%\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal"
if not exist "%ZIP%" ( echo ECHEC de la creation du zip. & pause & exit /b 1 )

if not defined GH goto manual

REM --- Connexion gh (une seule fois) ---
"%GH%" auth status >nul 2>&1
if errorlevel 1 (
  echo GitHub CLI n'est pas connecte : connexion ^(une seule fois^)...
  "%GH%" auth login --web --git-protocol https
  if errorlevel 1 ( echo Connexion echouee. & pause & exit /b 1 )
)

echo Push des commits...
git push
if errorlevel 1 ( echo ECHEC du push. & pause & exit /b 1 )

echo Publication de la Release %TAG%...
"%GH%" release create "%TAG%" "%ZIP%" --repo %REPO% --title "%TAG%" --notes-file "%NOTES%"
if errorlevel 1 ( echo ECHEC de la publication. & pause & exit /b 1 )
echo.
echo Release %TAG% publiee : https://github.com/%REPO%/releases/tag/%TAG%
timeout /t 5 >nul
exit /b 0

:manual
echo.
echo GitHub CLI ^(gh^) introuvable : creation manuelle.
echo  1. Le zip est dans le dossier Builds ^(il va s'ouvrir^) : glisser DigitalLogicSim-Windows.zip dans la zone de fichiers.
echo  2. La page GitHub s'ouvre avec la version et les notes pre-remplies : cliquer "Publish release".
for /f "usebackq delims=" %%U in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$b=[IO.File]::ReadAllText('%NOTES%'); if($b.Length -gt 6000){$b=$b.Substring(0,6000)}; Write-Output ([Uri]::EscapeDataString($b))"`) do set "BODY=%%U"
start "" "https://github.com/%REPO%/releases/new?tag=%TAG%&title=%TAG%&body=!BODY!"
start "" "%~dp0Builds"
pause
exit /b 0
