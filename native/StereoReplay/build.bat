@echo off
REM Builds SPTVRStereoReplay.dll (SPT-VR's stereo replay proof of concept, Ctrl+F9) with MSVC and copies it next to SPT-VR.dll in
REM the game (plugins\sptvr\native). Static CRT, so no VC++ redistributable is needed.
setlocal
set SCRIPT_DIR=%~dp0
set VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
set DEPLOY=F:\SPT4.1\BepInEx\plugins\sptvr\native

if not exist %VCVARS% ( echo [build] vcvars64.bat not found: adjust VCVARS. & exit /b 1 )
pushd "%SCRIPT_DIR%"
if not exist build mkdir build
call %VCVARS% >nul
if errorlevel 1 ( popd & echo [build] failed to init MSVC env. & exit /b 1 )
cl /nologo /LD /O2 /MT /EHsc /W3 StereoReplay.cpp /Fobuild\StereoReplay.obj /Fe:build\SPTVRStereoReplay.dll /link d3d11.lib dxguid.lib
if errorlevel 1 ( popd & echo [build] compile FAILED & exit /b 1 )
if "%1"=="nodeploy" ( popd & exit /b 0 )
if exist "F:\SPT4.1\BepInEx\plugins\sptvr" (
    if not exist "%DEPLOY%" mkdir "%DEPLOY%"
    copy /y build\SPTVRStereoReplay.dll "%DEPLOY%\SPTVRStereoReplay.dll" >nul && echo ==^> SPTVRStereoReplay.dll deployed to %DEPLOY%
)
popd
