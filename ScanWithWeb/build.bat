@echo off
echo ========================================
echo Building ScanWithWeb Service
echo ========================================

set OUTPUT_DIR=..\dist
set PROJECT=ScanWithWeb.csproj

:: Clean output directory
if exist %OUTPUT_DIR% rmdir /s /q %OUTPUT_DIR%
mkdir %OUTPUT_DIR%

echo.
echo [1/2] Building 64-bit version...
echo ----------------------------------------
dotnet publish %PROJECT% -c Release -r win-x64 -o %OUTPUT_DIR%\win-x64 --self-contained true
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: 64-bit build failed!
    exit /b 1
)

echo.
echo [2/2] Building 32-bit version...
echo ----------------------------------------
dotnet publish %PROJECT% -c Release -r win-x86 -o %OUTPUT_DIR%\win-x86 --self-contained true
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: 32-bit build failed!
    exit /b 1
)

echo.
echo ========================================
echo Build completed successfully!
echo ========================================
echo.
echo Output files:
echo   64-bit: %OUTPUT_DIR%\win-x64\ScanWithWeb.exe
echo   32-bit: %OUTPUT_DIR%\win-x86\ScanWithWeb.exe
echo.

pause
