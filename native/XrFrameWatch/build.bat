@echo off
REM Builds SPTVRXrFrameWatch.dll (watches Unity's OpenXR frame submission for SPT-VR's alternate-eye experiment, Ctrl+F11) with
REM MSVC and copies it next to SPT-VR.dll in the game (plugins\sptvr\native). Static CRT, so no VC++ redistributable is needed.
setlocal
set SCRIPT_DIR=%~dp0
set VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
set DEPLOY=F:\SPT4.1\BepInEx\plugins\sptvr\native

if not exist %VCVARS% ( echo [build] vcvars64.bat not found: adjust VCVARS. & exit /b 1 )
pushd "%SCRIPT_DIR%"
if not exist build mkdir build
call %VCVARS% >nul
if errorlevel 1 ( popd & echo [build] failed to init MSVC env. & exit /b 1 )
cl /nologo /LD /O2 /MT /EHa /W3 XrFrameWatch.cpp /Fobuild\XrFrameWatch.obj /Fe:build\SPTVRXrFrameWatch.dll
if errorlevel 1 ( popd & echo [build] compile FAILED & exit /b 1 )
if "%1"=="nodeploy" ( popd & exit /b 0 )
if exist "F:\SPT4.1\BepInEx\plugins\sptvr" (
    if not exist "%DEPLOY%" mkdir "%DEPLOY%"
    copy /y build\SPTVRXrFrameWatch.dll "%DEPLOY%\SPTVRXrFrameWatch.dll" >nul && echo ==^> SPTVRXrFrameWatch.dll deployed to %DEPLOY%
)
popd
