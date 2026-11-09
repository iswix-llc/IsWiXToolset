@echo off
rem Quick incremental build for the edit/test loop. Builds only the managed test projects and what they
rem reference (Core, Core.Burn, Core.WindowsInstaller, Core.Native, test packages). It skips the native Burn
rem and wixnative builds, Sdk publish, packaging, extensions, setup and the E2E data builds that
rem devbuild.cmd runs. No Visual Studio developer prompt is needed.
rem
rem   qb            builds WixToolsetTest.CoreIntegration (default)
rem   qb native     builds WixToolsetTest.Core.Native
rem   qb both       builds both
rem   qb ext        builds every extension test project with MSBuild (the extensions contain native C++
rem                 projects, which dotnet cannot build); the Visual Studio environment is loaded for you
rem
rem Run devbuild.cmd (full) instead when: you changed native code, dtf, burn, an extension or the Sdk,
rem or after a commit, because the version number comes from git height and the packages in
rem build\artifacts must match it (a restore error such as NU1102 means exactly that).
setlocal
pushd %~dp0

set _T=%1
if "%_T%"=="" set _T=core

if /i "%_T%"=="core" call :build src\wix\test\WixToolsetTest.CoreIntegration\WixToolsetTest.CoreIntegration.csproj || goto fail
if /i "%_T%"=="native" call :build src\wix\test\WixToolsetTest.Core.Native\WixToolsetTest.Core.Native.csproj || goto fail
if /i "%_T%"=="both" (
  call :build src\wix\test\WixToolsetTest.CoreIntegration\WixToolsetTest.CoreIntegration.csproj || goto fail
  call :build src\wix\test\WixToolsetTest.Core.Native\WixToolsetTest.Core.Native.csproj || goto fail
)

if /i "%_T%"=="ext" (
  call :devcmd || goto fail
  for /d %%d in (src\ext\*) do if exist "%%d\test" for /d %%t in ("%%d\test\WixToolsetTest.*") do call :buildms "%%t\%%~nxt.csproj" || goto fail
)

popd
endlocal
exit /b 0

rem Loads the Visual Studio developer environment once (same lookup as src\build_all.cmd).
:devcmd
if not "%VCToolsVersion%"=="" exit /b 0
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -version [17^,19^) -property installationPath`) do (
  if exist "%%i\Common7\Tools\vsdevcmd.bat" (
    call "%%i\Common7\Tools\vsdevcmd.bat" -no_logo
    exit /b 0
  )
)
echo Visual Studio developer command prompt not found
exit /b 2

:buildms
echo Building %~nx1
msbuild %1 -restore -p:Configuration=Debug -p:SuppressWixClean=true -m -nologo -v:m -tl:off
exit /b %errorlevel%

:build
rem Core.Native references wixnative.vcxproj, which dotnet build cannot load, so use MSBuild.
call :devcmd || exit /b
call :buildms %1
exit /b %errorlevel%

:fail
echo.
echo Quick build failed. If the error is a package restore problem (NU1102, NU1101), run devbuild.cmd once.
popd
endlocal
exit /b 1
