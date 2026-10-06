@echo off
REM Publie le build courant (Builds\Windows) comme Release GitHub, pour que lancerApp.bat puisse le
REM telecharger sur une machine sans Unity. Tout se fait en console, sans clic :
REM   Claude (API Anthropic) lit les commits depuis la derniere Release, redige les notes de version et
REM   propose le numero de version (V<majeur>.<mineur>.<correctif>) selon l'importance des changements ;
REM   Entree pour accepter, puis push des commits et creation de la Release avec le zip en piece jointe.
REM   Sans cle API : notes = liste brute des commits, version = mineur + 1 (V1.0.0 la premiere fois).
REM Usage : publierRelease.bat [version]      ex: publierRelease.bat V2.0.0   (sans argument : version proposee)
REM Requiert GitHub CLI (gh) connecte une fois avec :  gh auth login
REM Sans gh : le zip et les notes sont prepares et la page de creation de Release s'ouvre pre-remplie.
REM DRYRUN=1 dans l'environnement : tout sauf le push et la publication (pour tester le script).
setlocal EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"
set "SRC=%~dp0Builds\Windows"
set "ZIP=%~dp0Builds\DigitalLogicSim-Windows.zip"
set "ZIPMAC=%~dp0Builds\DigitalLogicSim-Mac.zip"
set "ZIPLINUX=%~dp0Builds\DigitalLogicSim-Linux.zip"
set "NOTES=%~dp0Builds\release-notes.md"
set "COMMITS=%~dp0Builds\release-commits.txt"
set "VERSIONFILE=%~dp0Builds\release-version.txt"
set "REPO=lucasBertola/Digital-Logic-Sim"

if not exist "%SRC%\DigitalLogicSim.exe" ( echo Aucun build dans Builds\Windows : compile d'abord ^(Tools ^> Build Windows Player^). & pause & exit /b 1 )

REM --- Banc de regression : rien n'est publie si un test echoue (NOTESTS=1 pour sauter, deconseille) ---
if not defined NOTESTS (
  call "%~dp0runTests.bat"
  if errorlevel 1 ( echo. & echo Le banc de test echoue : publication annulee. & pause & exit /b 1 )
)

REM --- gh : dans le PATH, sinon aux emplacements d'installation habituels ---
set "GH="
where gh >nul 2>&1 && set "GH=gh"
if not defined GH if exist "%LOCALAPPDATA%\Programs\gh\bin\gh.exe" set "GH=%LOCALAPPDATA%\Programs\gh\bin\gh.exe"
if not defined GH if exist "%ProgramFiles%\GitHub CLI\gh.exe" set "GH=%ProgramFiles%\GitHub CLI\gh.exe"

REM --- Derniere Release publiee (LAST) et version de repli (NEXT = mineur + 1, ou V1.0.0) ---
set "LAST="
set "NEXT="
for /f "usebackq tokens=1,2 delims=|" %%V in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $h=@{'User-Agent'='DigitalLogicSim'}; try { $r=Invoke-RestMethod 'https://api.github.com/repos/%REPO%/releases/latest' -Headers $h; $t=[string]$r.tag_name } catch { $t='' }; if($t -match '^V?(\d+)\.(\d+)\.(\d+)$'){ $n='V'+$matches[1]+'.'+([int]$matches[2]+1)+'.0' } elseif($t -match '(\d+)'){ $n='V'+([int]$matches[1]+1)+'.0.0' } else { $n='V1.0.0' }; Write-Output ($n + '|' + $t)"`) do ( set "NEXT=%%V" & set "LAST=%%W" )
if not defined NEXT set "NEXT=V1.0.0"

REM --- Commits depuis la derniere Release (ou depuis le debut du fork) ---
if defined LAST (
  REM le tag de la Release est cree sur GitHub : il faut le rapatrier pour que git connaisse la plage
  git fetch -q --tags origin >nul 2>&1
  git rev-parse --verify -q "%LAST%^{commit}" >nul 2>&1
  if errorlevel 1 (
    echo ^(tag %LAST% introuvable localement : notes depuis le debut du fork^)
    set "RANGE=HEAD"
    git rev-parse --verify -q upstream/main >nul 2>&1 && set "RANGE=upstream/main..HEAD"
  ) else (
    set "RANGE=%LAST%..HEAD"
  )
  set "TITLE=Changements depuis %LAST%"
) else (
  set "RANGE=HEAD"
  git rev-parse --verify -q upstream/main >nul 2>&1 && set "RANGE=upstream/main..HEAD"
  set "TITLE=Premiere version"
)
git log !RANGE! --no-merges --pretty=format:"* %%s%%n%%b" > "%COMMITS%"

REM Notes de repli : un point par commit
> "%NOTES%" echo ## !TITLE!
>> "%NOTES%" echo.
git log !RANGE! --no-merges --pretty=format:"- %%s" >> "%NOTES%"
>> "%NOTES%" echo.

REM --- Claude : notes redigees + numero de version propose (si une cle API est disponible) ---
for /f "usebackq delims=" %%K in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$k=[Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','Machine'); if(-not $k){$k=[Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','User')}; if(-not $k){$k=$env:ANTHROPIC_API_KEY}; Write-Output $k"`) do set "ANTHROPIC_API_KEY=%%K"
if defined ANTHROPIC_API_KEY (
  echo Claude lit les commits, redige les notes et propose la version...
  if exist "%VERSIONFILE%" del "%VERSIONFILE%"
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\releaseNotes.ps1" -CommitsFile "%COMMITS%" -OutFile "%NOTES%" -VersionFile "%VERSIONFILE%" -LastTag "%LAST%"
  if exist "%VERSIONFILE%" ( set /p NEXT=<"%VERSIONFILE%" ) else ( echo ^(echec : liste brute des commits et version %NEXT% conservees^) )
) else (
  echo ^(ANTHROPIC_API_KEY absente : notes = liste brute des commits, version proposee = %NEXT%^)
)

set "TAG=%~1"
if "%TAG%"=="" (
  echo.
  echo ---- Notes de version ----
  type "%NOTES%"
  echo --------------------------
  echo.
  set /p "TAG=Version a publier [!NEXT!] : "
)
if "!TAG!"=="" set "TAG=!NEXT!"
echo.
echo ==== !TAG! ====

REM --- Le tag est cree AVANT le build, pour que le bandeau de version de l'app affiche exactement !TAG! ---
set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.0.46f1\Editor\Unity.exe"
if not exist "%UNITY%" ( echo Editeur Unity introuvable : %UNITY% & pause & exit /b 1 )
tasklist /FI "IMAGENAME eq DigitalLogicSim.exe" 2>nul | find /I "DigitalLogicSim.exe" >nul && ( echo Fermeture de l'application en cours... & taskkill /IM DigitalLogicSim.exe /F >nul 2>&1 & timeout /t 2 >nul )
REM Un tag local absent de GitHub = reste d'une publication interrompue (fenetre fermee pendant le build) : on le refait.
if not defined DRYRUN (
  git rev-parse -q --verify "refs/tags/!TAG!" >nul 2>&1 && (
    git ls-remote --exit-code --tags origin "refs/tags/!TAG!" >nul 2>&1 || ( echo Tag !TAG! local d'une publication interrompue : supprime. & git tag -d "!TAG!" >nul )
  )
  git tag -a "!TAG!" -m "!TAG!"
  if errorlevel 1 ( echo ECHEC de la creation du tag !TAG! ^(existe deja ?^). & pause & exit /b 1 )
)
echo Build de la version !TAG! pour Windows, Mac et Linux ^(quelques minutes^)...
"%UNITY%" -projectPath "%~dp0." -executeMethod BuildTools.BuildRelease -quit -logFile "%~dp0Builds\build.log"
findstr /C:"RELEASE BUILD SUCCEEDED" "%~dp0Builds\build.log" >nul 2>&1
if errorlevel 1 goto buildfailed
REM --- l'app buildee elle-meme doit passer son auto-test (le build retire du code que l'editeur garde) ---
call "%~dp0runPlayerSelfTest.bat"
if errorlevel 1 (
  echo L'auto-test de l'application buildee echoue : publication annulee.
  if not defined DRYRUN git tag -d "!TAG!" >nul 2>&1
  pause & exit /b 1
)
findstr /C:"RELEASE BUILD SUCCEEDED" "%~dp0Builds\build.log" >nul 2>&1
:buildfailed
if errorlevel 1 (
  echo ECHEC du build ^(voir Builds\build.log^).
  if not defined DRYRUN git tag -d "!TAG!" >nul 2>&1
  pause & exit /b 1
)

tasklist /FI "IMAGENAME eq DigitalLogicSim.exe" 2>nul | find /I "DigitalLogicSim.exe" >nul && ( echo Fermeture de l'application en cours... & taskkill /IM DigitalLogicSim.exe /F >nul 2>&1 & timeout /t 2 >nul )
REM L'historique des conversations Claude et la cle Anthropic ne partent JAMAIS dans une release.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f = Get-ChildItem -LiteralPath '%SRC%','%~dp0Builds\Mac','%~dp0Builds\Linux' -Recurse -File | Where-Object { $_.Name -in 'AskClaudeConversation.json','anthropic_key.txt' }; if ($f) { $f.FullName; exit 1 }"
if errorlevel 1 (
  echo REFUS : fichiers prives ^(conversation Claude / cle^) dans le build : rien n'est publie.
  if not defined DRYRUN git tag -d "!TAG!" >nul 2>&1
  pause & exit /b 1
)
echo Creation du zip...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; if(Test-Path '%ZIP%'){Remove-Item '%ZIP%'}; Compress-Archive -Path '%SRC%\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal"
if not exist "%ZIP%" ( echo ECHEC de la creation du zip. & pause & exit /b 1 )
if not exist "%ZIPMAC%" ( echo Zip Mac absent ^(voir Builds\build.log^). & pause & exit /b 1 )
if not exist "%ZIPLINUX%" ( echo Zip Linux absent ^(voir Builds\build.log^). & pause & exit /b 1 )
REM --- comment lancer sur Mac / Linux, a la fin des notes ---
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\releaseDownloads.ps1" "%NOTES%"

if defined DRYRUN ( echo [DRYRUN] pas de push ni de publication. Version !TAG!, notes dans %NOTES% & exit /b 0 )
if not defined GH goto manual

REM --- Connexion gh (une seule fois) ---
"%GH%" auth status >nul 2>&1
if errorlevel 1 (
  echo GitHub CLI n'est pas connecte : connexion ^(une seule fois^)...
  "%GH%" auth login --web --git-protocol https
  if errorlevel 1 ( echo Connexion echouee. & pause & exit /b 1 )
)

echo Push des commits et du tag...
git push
if errorlevel 1 ( echo ECHEC du push. & pause & exit /b 1 )
git push origin "!TAG!"
if errorlevel 1 ( echo ECHEC du push. & pause & exit /b 1 )

echo Publication de la Release !TAG!...
"%GH%" release create "!TAG!" "%ZIP%" "%ZIPMAC%" "%ZIPLINUX%" --repo %REPO% --title "!TAG!" --notes-file "%NOTES%"
if errorlevel 1 ( echo ECHEC de la publication. & pause & exit /b 1 )
echo.
echo Release !TAG! publiee : https://github.com/%REPO%/releases/tag/!TAG!
timeout /t 5 >nul
exit /b 0

:manual
echo.
echo GitHub CLI ^(gh^) introuvable : creation manuelle.
echo  1. Le zip est dans le dossier Builds ^(il va s'ouvrir^) : glisser les 3 zips ^(Windows, Mac, Linux^) dans la zone de fichiers.
echo  2. La page GitHub s'ouvre avec la version et les notes pre-remplies : cliquer "Publish release".
for /f "usebackq delims=" %%U in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$b=[IO.File]::ReadAllText('%NOTES%'); if($b.Length -gt 6000){$b=$b.Substring(0,6000)}; Write-Output ([Uri]::EscapeDataString($b))"`) do set "BODY=%%U"
start "" "https://github.com/%REPO%/releases/new?tag=!TAG!&title=!TAG!&body=!BODY!"
start "" "%~dp0Builds"
pause
exit /b 0
