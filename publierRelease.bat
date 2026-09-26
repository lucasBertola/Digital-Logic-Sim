@echo off
REM Publie le build courant (Builds\Windows) comme Release GitHub, pour que lancerApp.bat puisse le
REM telecharger sur une machine sans Unity.
REM Usage : publierRelease.bat [version]     ex: publierRelease.bat v0.2
REM  - Avec GitHub CLI (gh) installe et connecte : la Release est creee directement.
REM  - Sinon : le zip est prepare et la page de creation de Release s'ouvre (joindre le zip, publier).
setlocal
cd /d "%~dp0"
set "SRC=%~dp0Builds\Windows"
set "ZIP=%~dp0Builds\DigitalLogicSim-Windows.zip"
set "REPO=lucasBertola/Digital-Logic-Sim"

if not exist "%SRC%\DigitalLogicSim.exe" ( echo Aucun build dans Builds\Windows : compile d'abord ^(Tools ^> Build Windows Player^). & pause & exit /b 1 )

set "TAG=%~1"
if "%TAG%"=="" set /p TAG=Numero de version (ex: v0.2) :
if "%TAG%"=="" ( echo Version requise. & exit /b 1 )

echo Creation du zip...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; if(Test-Path '%ZIP%'){Remove-Item '%ZIP%'}; Compress-Archive -Path '%SRC%\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal"
if not exist "%ZIP%" ( echo ECHEC de la creation du zip. & pause & exit /b 1 )
echo Zip pret : %ZIP%

where gh >nul 2>&1
if errorlevel 1 (
  echo.
  echo GitHub CLI ^(gh^) n'est pas installe : creation manuelle.
  echo  1. Le zip est dans le dossier Builds ^(il va s'ouvrir^).
  echo  2. Sur la page GitHub qui s'ouvre : version %TAG%, glisser le zip dans la zone de fichiers, "Publish release".
  start "" "https://github.com/%REPO%/releases/new?tag=%TAG%&title=%TAG%"
  start "" "%~dp0Builds"
  pause
  exit /b 0
)

gh release create "%TAG%" "%ZIP%" --repo %REPO% --title "%TAG%" --generate-notes
if errorlevel 1 ( echo ECHEC de la publication. & pause & exit /b 1 )
echo Release %TAG% publiee : https://github.com/%REPO%/releases/tag/%TAG%
pause
endlocal
