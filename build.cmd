@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

if not exist dist mkdir dist

set "OUT=%~1"
if "%OUT%"=="" set "OUT=NetSplitter.exe"

set "ICON="
if exist src\app.ico set "ICON=-win32icon:src\app.ico"

"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 %ICON% ^
  -out:dist\%OUT% -win32manifest:src\app.manifest ^
  -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll ^
  -r:System.Drawing.dll -r:System.Management.dll -r:System.Web.Extensions.dll ^
  src\*.cs

if errorlevel 1 (
  echo [ERROR] Build failed.
  exit /b 1
)

echo [OK] dist\%OUT%
exit /b 0
