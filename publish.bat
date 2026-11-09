@echo off
REM Builds the mod, stages it into workshop\content, and uploads with MegaCrit's ModUploader.
setlocal
cd /d "%~dp0"

set UPLOADER=ModUploader-win-x64\ModUploader.exe
if not exist "%UPLOADER%" set UPLOADER=ModUploader.exe
if not exist "%UPLOADER%" (
  echo ModUploader.exe not found. Download it from the sts2-mod-uploader releases page.
  pause & exit /b 1
)

where dotnet >nul 2>&1 || (echo dotnet not found on PATH. Install the .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9.0 & pause & exit /b 1)
dotnet --list-sdks | findstr /b "9." >nul || (echo No .NET 9 SDK installed - only runtimes. Install the SDK from https://dotnet.microsoft.com/download/dotnet/9.0 & pause & exit /b 1)

dotnet build -c Release || (echo BUILD FAILED & pause & exit /b 1)

if exist workshop\content rmdir /s /q workshop\content
mkdir workshop\content
copy /y bin\Release\HextechRelicStats.dll workshop\content\ >nul || (pause & exit /b 1)
copy /y HextechRelicStats.json workshop\content\ >nul || (pause & exit /b 1)

REM Run from the uploader's folder so it finds steam_api64.dll / steam_appid.txt. Steam must be running.
set UPDIR=%~dp0ModUploader-win-x64
if not exist "%UPDIR%\ModUploader.exe" set UPDIR=%~dp0.
pushd "%UPDIR%"
.\ModUploader.exe upload -w "%~dp0workshop"
set RC=%ERRORLEVEL%
popd
echo.
echo Uploader exit code: %RC%
if exist workshop\mod_id.txt (echo Item ID file: workshop\mod_id.txt) else (echo No workshop\mod_id.txt - upload did not complete. Check mod-uploader.log next to ModUploader.exe.)
pause
