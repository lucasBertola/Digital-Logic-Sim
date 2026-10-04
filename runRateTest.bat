@echo off
REM Vitesse de l'application BUILDEE (rendu, thread de simulation, tout) sur une COPIE d'un de tes projets :
REM   runRateTest.bat                -> projet PC, chip CPU_2, en RUN FAST
REM   runRateTest.bat PC CPU_2 gates -> sans RUN FAST
REM La copie s'appelle _RateTest_<projet> dans le dossier de sauvegarde et est supprimee ensuite : ton projet n'est
REM jamais touche. Rapport : vitesse et images/s chaque seconde, ramasse-miettes, LCD qui suit le jeu (Espace appuye
REM a 2,5 s), profil du thread de simulation.
setlocal
set "EXE=%~dp0Builds\Windows\DigitalLogicSim.exe"
set "SAVE=%USERPROFILE%\AppData\LocalLow\SebastianLague\Digital-Logic-Sim"
set "PROJ=%~1"
if "%PROJ%"=="" set "PROJ=PC"
set "CHIP=%~2"
if "%CHIP%"=="" set "CHIP=CPU_2"
set "EXTRA="
if /I "%~3"=="gates" set "EXTRA=-ratetest-gates"
if not exist "%EXE%" ( echo Pas de build : %EXE% & exit /b 2 )
if not exist "%SAVE%\Projects\%PROJ%" ( echo Pas de projet %PROJ% dans %SAVE%\Projects & exit /b 2 )
set "COPY=%SAVE%\Projects\_RateTest_%PROJ%"
if exist "%COPY%" rmdir /s /q "%COPY%"
robocopy "%SAVE%\Projects\%PROJ%" "%COPY%" /E /NFL /NDL /NJH /NJS /NP >nul
if exist "%SAVE%\ratetest.txt" del "%SAVE%\ratetest.txt"
start "" /wait "%EXE%" -ratetest "_RateTest_%PROJ%" "%CHIP%" %EXTRA%
if exist "%COPY%" rmdir /s /q "%COPY%"
if exist "%SAVE%\ratetest.txt" ( type "%SAVE%\ratetest.txt" ) else ( echo Aucun rapport produit. & exit /b 3 )
