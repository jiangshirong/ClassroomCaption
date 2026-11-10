@echo off
setlocal
set "PROJECT_ROOT=%~dp0"
set "DOTNET_ROOT=%PROJECT_ROOT%.tools\dotnet"
set "APP_EXE=%PROJECT_ROOT%artifacts\development\Release\net10.0-windows\ClassroomCaption.exe"
if /i "%~1"=="--check" (
    echo Runtime: %DOTNET_ROOT%
    echo App: %APP_EXE%
    echo Data: automatic drive selection
    exit /b 0
)
wscript.exe "%~dpn0.vbs" %*
exit /b 0

:buildfailed
echo Development build failed. No older executable was launched.
pause
exit /b 1

:missing
echo The local .NET runtime is missing.
pause
exit /b 1
