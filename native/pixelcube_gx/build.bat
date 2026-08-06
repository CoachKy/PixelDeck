@echo off
cd /d "%~dp0"
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
set "CM=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "NJ=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
"%CM%" -S . -B build -G Ninja -DCMAKE_MAKE_PROGRAM="%NJ%" -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1
"%CM%" --build build
echo [pixelcube_gx] Native build completed successfully.
