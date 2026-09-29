@echo off
REM Auto-test DANS l'application buildee (Builds\Windows\DigitalLogicSim.exe) : le build retire du code que
REM l'editeur Unity garde, un bug peut donc n'exister que dans l'exe. Utilise une copie FIGEE du projet de test
REM (TestData\Bench\PC), posee dans le dossier de sauvegarde sous le nom _SelfTest_PC puis supprimee : tes projets ne sont jamais touches.
REM   runPlayerSelfTest.bat          -> tests hors ligne
REM   runPlayerSelfTest.bat claude   -> plus une vraie analyse memoire par Claude (payant, ~1 min)
setlocal
set "EXE=%~dp0Builds\Windows\DigitalLogicSim.exe"
set "REPORT=%USERPROFILE%\AppData\LocalLow\SebastianLague\Digital-Logic-Sim\selftest.txt"
if not exist "%EXE%" ( echo Pas de build : %EXE% & exit /b 2 )
if exist "%REPORT%" del "%REPORT%"
set "TESTPROJ=%USERPROFILE%\AppData\LocalLow\SebastianLague\Digital-Logic-Sim\Projects\_SelfTest_PC"
if exist "%TESTPROJ%" rmdir /s /q "%TESTPROJ%"
robocopy "%~dp0TestData\Bench\PC" "%TESTPROJ%" /E /NFL /NDL /NJH /NJS /NP >nul

set "ARGS=-selftest"
if /I "%~1"=="claude" set "ARGS=-selftest -selftest-claude"
start "" /wait "%EXE%" %ARGS%
set "CODE=%ERRORLEVEL%"
if exist "%TESTPROJ%" rmdir /s /q "%TESTPROJ%"
if exist "%REPORT%" ( type "%REPORT%" ) else ( echo Aucun rapport produit. & exit /b 3 )
exit /b %CODE%
