@echo off
rem Builds UE Launcher into publish\UELauncher.exe. Needs the .NET 9 (or newer) SDK.
setlocal EnableExtensions

rem Explorer starts a double-clicked script as: cmd /c ""path" "  (ends with space + quote). Only then keep the window open at the end.
setlocal EnableDelayedExpansion
set "IS_DC="
if "!cmdcmdline:~-2,1!"==" " set "IS_DC=1"
endlocal & set "DOUBLECLICK=%IS_DC%"
set "RESULT=0"

cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 goto :nosdk
dotnet --list-sdks 2>nul | findstr /r "^9\. ^[1-9][0-9]\." >nul
if errorlevel 1 goto :nosdk

tasklist /fi "imagename eq UELauncher.exe" /nh 2>nul | find /i "UELauncher.exe" >nul
if not errorlevel 1 goto :running

echo Building UE Launcher...
dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish -nologo -v q
if errorlevel 1 goto :failed

echo.
echo Built: %~dp0publish\UELauncher.exe
goto :done

:nosdk
echo The .NET 9 SDK (or newer) was not found. Install it from https://dotnet.microsoft.com/download
set "RESULT=2"
goto :done

:running
echo UE Launcher is running. Close it and run this again, because the build replaces its exe.
set "RESULT=3"
goto :done

:failed
echo.
echo Build FAILED.
set "RESULT=1"

:done
if defined DOUBLECLICK pause
exit /b %RESULT%
