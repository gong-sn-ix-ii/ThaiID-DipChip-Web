@echo off
rem Builds ThaiIdBridge.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio / .NET SDK needed.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [x] .NET Framework 4.x C# compiler not found.
  exit /b 1
)

if not exist dist mkdir dist
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ /warn:4 ^
  /out:dist\ThaiIdBridge.exe ^
  /r:System.Web.Extensions.dll /r:System.Drawing.dll ^
  src\*.cs
if errorlevel 1 (
  echo [x] Build failed.
  exit /b 1
)

if not exist dist\ThaiIdBridge.config.json copy /y ThaiIdBridge.config.json dist\ >nul

rem Bundle the Angular build (if present) so the bridge can also serve the web app at http://127.0.0.1:8424/
if exist "..\web\dist\thai-id-web\browser\index.html" (
  if exist dist\www rmdir /s /q dist\www
  xcopy /e /i /q /y "..\web\dist\thai-id-web\browser" dist\www >nul
  echo [ok] Copied web app into dist\www
)

echo [ok] Built dist\ThaiIdBridge.exe
endlocal
