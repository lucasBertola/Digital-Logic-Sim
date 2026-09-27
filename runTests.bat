@echo off
REM Banc de regression du simulateur (Assets\Editor\Bench). Lance l'editeur Unity en mode interactif, execute
REM tous les cas en parallele et affiche le resume. Code de sortie 1 si un cas echoue.
REM   runTests.bat            -> verification
REM   runTests.bat record     -> (re)enregistre les references (goldens) des projets de TestData\Bench
setlocal
cd /d "%~dp0"
set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.0.46f1\Editor\Unity.exe"
set "METHOD=RegressionBench.Run"
if /I "%~1"=="record" set "METHOD=RegressionBench.Record"
if not exist "%UNITY%" ( echo Editeur Unity introuvable : %UNITY% & exit /b 2 )
tasklist /FI "IMAGENAME eq Unity.exe" 2>nul | findstr /I "Unity.exe" >nul && ( echo Un editeur Unity est deja ouvert : ferme-le dabord. & exit /b 2 )
if not exist "Builds" mkdir "Builds"
del /q "Builds\tests-summary.txt" 2>nul

echo Lancement du banc (%METHOD%)...
"%UNITY%" -projectPath "%~dp0." -executeMethod DLS.Bench.%METHOD% -quit -logFile "%~dp0Builds\tests.log"
set "CODE=%ERRORLEVEL%"

findstr /C:"error CS" "Builds\tests.log" >nul 2>&1 && (
  echo ERREURS DE COMPILATION :
  findstr /C:"error CS" "Builds\tests.log"
  exit /b 3
)
if exist "Builds\tests-summary.txt" ( type "Builds\tests-summary.txt" ) else ( echo Aucun resume produit : voir Builds\tests.log & exit /b 3 )
exit /b %CODE%
