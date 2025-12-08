@echo off
echo ========================================
echo Building ScanWithWeb Installers
echo ========================================
echo.

:: Check if Inno Setup is installed
set ISCC="%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist %ISCC% (
    set ISCC="%ProgramFiles%\Inno Setup 6\ISCC.exe"
)
if not exist %ISCC% (
    echo ERROR: Inno Setup 6 not found!
    echo Please download and install from: https://jrsoftware.org/isinfo.php
    echo.
    pause
    exit /b 1
)

:: Create output directory
if not exist "..\dist\installer" mkdir "..\dist\installer"

echo [1/4] Building 64-bit application...
echo ----------------------------------------
cd ..\ScanWithWeb
dotnet publish ScanWithWeb.csproj -c Release -r win-x64 -o ..\dist\win-x64 --self-contained true
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: 64-bit build failed!
    pause
    exit /b 1
)

echo.
echo [2/4] Building 32-bit application...
echo ----------------------------------------
dotnet publish ScanWithWeb.csproj -c Release -r win-x86 -o ..\dist\win-x86 --self-contained true
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: 32-bit build failed!
    pause
    exit /b 1
)

cd ..\ScanWithWeb_setup

echo.
echo [3/4] Creating 64-bit installer...
echo ----------------------------------------
%ISCC% ScanWithWeb_x64.iss
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: 64-bit installer creation failed!
    pause
    exit /b 1
)

echo.
echo [4/4] Creating 32-bit installer...
echo ----------------------------------------
%ISCC% ScanWithWeb_x86.iss
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: 32-bit installer creation failed!
    pause
    exit /b 1
)

echo.
echo ========================================
echo Build completed successfully!
echo ========================================
echo.
echo Output files:
echo   64-bit: ..\dist\installer\ScanWithWeb_Setup_x64_v2.0.3.exe
echo   32-bit: ..\dist\installer\ScanWithWeb_Setup_x86_v2.0.3.exe
echo.

pause
