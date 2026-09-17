@echo off
setlocal enabledelayedexpansion
REM ---------------------------------------------------------------------------
REM  Builds Anything Search packages.
REM
REM    build-store-package_AnythingSearch.cmd            both      (default)
REM    build-store-package_AnythingSearch.cmd store      Store upload only
REM    build-store-package_AnythingSearch.cmd sideload   installable package only
REM
REM  Two outputs, and they are NOT interchangeable:
REM
REM    *.msixupload    UNSIGNED - upload this to Partner Center. The Store signs
REM                    it. Windows cannot install it.
REM    *.msixbundle    SIGNED with the certificate named in WinAppPakaging.wapproj
REM                    (CN=8545CA03-... , already trusted on this machine) -
REM                    install this locally to test.
REM
REM  Building both takes two passes, because one package cannot be signed and
REM  unsigned at once. The default does both so neither is ever missing.
REM
REM  "Associate App with the Store" in Visual Studio is NOT needed. Its only job
REM  is writing the Store identity into Package.appxmanifest, already correct:
REM      Name       4789ZeroByte.AnythingSearch
REM      Publisher  CN=8545CA03-A40F-42F8-9E80-790ABC2452FC
REM      PubDisplay Zero Byte
REM
REM  Raise Version in WinAppPakaging\Package.appxmanifest before each submission -
REM  the Store rejects a version it has already accepted. Keep it in step with
REM  AnythingSearch.csproj (Version/FileVersion/AssemblyVersion) and with
REM  Helper\AppConfig.AppVersion / AppReleaseVersion.
REM
REM  Platform is AnyCPU / bundle "neutral" - it must match AppxBundlePlatforms in
REM  the .wapproj and the neutral package already published to the Store. The app
REM  itself publishes self-contained win-x64 (see AnythingSearch.csproj); the
REM  neutral bundle is only the wrapper.
REM ---------------------------------------------------------------------------

set MODE=%~1
if /i "%MODE%"=="" set MODE=both
if /i not "%MODE%"=="both" if /i not "%MODE%"=="store" if /i not "%MODE%"=="sideload" (
  echo Usage: %~nx0 [both^|store^|sideload]
  exit /b 1
)

set ROOT=%~dp0
set ROOT=%ROOT:~0,-1%
set APPPROJ=%ROOT%\AnythingSearch
set WAPPROJ=%ROOT%\WinAppPakaging

if not exist "%WAPPROJ%\WinAppPakaging.wapproj" (
  echo ERROR: packaging project not found: %WAPPROJ%\WinAppPakaging.wapproj
  exit /b 1
)

REM ---------------------------------------------------------------------------
REM  Pre-flight. These are things that are invisible in the finished package and
REM  uncorrectable without another certification pass.
REM ---------------------------------------------------------------------------
echo Pre-flight checks...

REM  The install telemetry must post to the production endpoint. A build left
REM  pointing at apiUrlLocal (http://localhost:85) silently records nothing for
REM  every user who installs it.
findstr /c:"AppConfig.apiUrlLocal" "%APPPROJ%\DeviceData\DeviceInfoCollector.cs" >nul
if not errorlevel 1 (
  echo.
  echo ERROR: DeviceInfoCollector posts to AppConfig.apiUrlLocal.
  echo        Switch it back to AppConfig.apiUrlProd before packaging.
  exit /b 1
)

echo   Telemetry endpoint = prod ...... ok
echo.
echo   App data folder - a change here makes every install re-index from scratch:
for /f "tokens=*" %%v in ('findstr /c:"AppDataFolderName =" "%APPPROJ%\Helper\AppConfig.cs"') do echo     %%v
echo.
echo   App version - keep these three in step:
for /f "tokens=*" %%v in ('findstr /c:"AppVersion =" "%APPPROJ%\Helper\AppConfig.cs"') do echo     %%v
for /f "tokens=*" %%v in ('findstr /c:"<Version>" "%APPPROJ%\AnythingSearch.csproj"') do echo     %%v
echo.
echo   Packaging identity - check this is what you meant to upload:
for /f "tokens=*" %%v in ('findstr /c:"4789ZeroByte" "%WAPPROJ%\Package.appxmanifest"') do echo     %%v
REM  Identity/Version only - drop MinVersion, MaxVersionTested, manifestVersion.
for /f "tokens=*" %%v in ('findstr /c:"Version=" "%WAPPROJ%\Package.appxmanifest" ^| findstr /v /i /c:"MinVersion" /c:"MaxVersionTested" /c:"manifestVersion"') do echo     %%v
echo.

tasklist /fi "imagename eq AnythingSearch.exe" 2>nul | findstr /i "AnythingSearch.exe" >nul
if not errorlevel 1 (
  echo   WARNING: AnythingSearch.exe is running - it may hold the build output and
  echo            fail the copy. This also fires for an installed copy from the
  echo            Store, which runs from the system tray.
  echo.
)

REM --- locate MSBuild (a .wapproj cannot be built by "dotnet build")
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
if not exist "%VSWHERE%" set VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -property installationPath`) do set VSPATH=%%i
set MSBUILD=%VSPATH%\MSBuild\Current\Bin\MSBuild.exe
if not exist "%MSBUILD%" ( echo ERROR: MSBuild not found - install the VS MSBuild component. & exit /b 1 )

REM --- clear obj once. Not hygiene: the Release build is self-contained and
REM     trimmed while Debug is neither, so a stale ResolvePackageAssets cache
REM     from an F5 run can ship a package missing e_sqlite3.dll or a trimmed
REM     assembly, which crashes on launch rather than at build time.
echo Clearing intermediate output...
if exist "%APPPROJ%\obj"          rmdir /s /q "%APPPROJ%\obj"
if exist "%WAPPROJ%\obj"          rmdir /s /q "%WAPPROJ%\obj"
if exist "%WAPPROJ%\AppPackages"  rmdir /s /q "%WAPPROJ%\AppPackages"

echo Restoring...
"%MSBUILD%" "%WAPPROJ%\WinAppPakaging.wapproj" -t:Restore -p:Configuration=Release -p:Platform=AnyCPU -v:q -nologo || exit /b 1

if /i not "%MODE%"=="sideload" (
  echo.
  echo [1/2] Store upload package ^(unsigned^)...
  call :build StoreUpload false || exit /b 1
  REM The Store pass also drops an unsigned _Test bundle. It cannot be
  REM installed, and leaving it invites exactly that mistake; the signed one
  REM from the sideload pass replaces it.
  for /d %%d in ("%WAPPROJ%\AppPackages\*_Test") do rmdir /s /q "%%d"
)

if /i not "%MODE%"=="store" (
  echo.
  echo [2/2] Installable package ^(signed^)...
  call :build SideloadOnly true || exit /b 1
)

echo.
echo ===============================================================
for %%f in ("%WAPPROJ%\AppPackages\*.msixupload") do (
  echo  STORE  upload to Partner Center ^> Packages:
  echo     %%~ff
)
for /r "%WAPPROJ%\AppPackages" %%f in (*.msixbundle) do (
  echo  LOCAL  signed, installable:
  echo     %%~ff
  echo     install:   powershell -Command "Add-AppxPackage -Path '%%~ff'"
  echo     reinstall: powershell -Command "Remove-AppxPackage -Package (Get-AppxPackage *AnythingSearch*).PackageFullName"
)
echo ===============================================================
endlocal
exit /b 0

:build
"%MSBUILD%" "%WAPPROJ%\WinAppPakaging.wapproj" ^
  -p:Configuration=Release ^
  -p:Platform=AnyCPU ^
  -p:UapAppxPackageBuildMode=%~1 ^
  -p:AppxBundle=Always ^
  -p:AppxBundlePlatforms=neutral ^
  -p:AppxPackageSigningEnabled=%~2 ^
  -v:m -nologo
exit /b %errorlevel%
