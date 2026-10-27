@echo off
REM Builds SPTVRGpuProbe.dll (GPU timing for SPT-VR's StereoCostProbe, Ctrl+F10) with MSVC and copies it next to
REM SPT-VR.dll in the game (plugins\sptvr\native). Static CRT, so no VC++ redistributable is needed.
setlocal
set SCRIPT_DIR=%~dp0
set VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
set DEPLOY=F:\SPT4.1\BepInEx\plugins\sptvr\native

if not exist %VCVARS% ( echo [build] vcvars64.bat not found: adjust VCVARS. & exit /b 1 )
pushd "%SCRIPT_DIR%"
if not exist build mkdir build
call %VCVARS% >nul
if errorlevel 1 ( popd & echo [build] failed to init MSVC env. & exit /b 1 )
cl /nologo /LD /O2 /MT /EHsc /W3 GpuProbe.cpp /Fobuild\GpuProbe.obj /Fe:build\SPTVRGpuProbe.dll /link d3d11.lib
if errorlevel 1 ( popd & echo [build] compile FAILED & exit /b 1 )
if exist "F:\SPT4.1\BepInEx\plugins\sptvr" (
    if not exist "%DEPLOY%" mkdir "%DEPLOY%"
    copy /y build\SPTVRGpuProbe.dll "%DEPLOY%\SPTVRGpuProbe.dll" >nul && echo ==^> SPTVRGpuProbe.dll deployed to %DEPLOY%
)
popd
