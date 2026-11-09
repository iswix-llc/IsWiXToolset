@setlocal
@pushd %~dp0

@set _RESULT=0
@set _C=Debug
@set _L=%~dp0..\..\..\build\logs
:parse_args
@if /i "%1"=="release" set _C=Release
@if /i "%1"=="test" set RuntimeTestsEnabled=true
@if not "%1"=="" shift & goto parse_args

@echo Msi integration tests %_C%

msbuild -t:Build -Restore -p:Configuration=%_C% -tl -nologo -warnaserror -bl:%_L%\test_msi_build.binlog || exit /b
msbuild -t:Build -Restore TestData\TestData.proj -p:Configuration=%_C% -tl -nologo -m -warnaserror -bl:%_L%\test_msi_data_build.binlog || exit /b

@if not "%RuntimeTestsEnabled%"=="true" goto :LExit
@if not "%WixE2ESuite%"=="" if /i not "%WixE2ESuite:~0,3%"=="msi" goto :LExit

:: WixE2ESuite is msi (everything), msi-a (the Util user tests) or msi-b (everything else), so that the two
:: halves can run in parallel CI jobs.
@set _S=
@if not "%WixE2ESuite%"=="" set "_S=.%WixE2ESuite%"
@set "_F=FullyQualifiedName~WixToolsetTest.MsiE2E"
@if /i "%WixE2ESuite%"=="msi-a" set "_F=FullyQualifiedName~UtilExtensionUserTests"
@if /i "%WixE2ESuite%"=="msi-b" set "_F=FullyQualifiedName!~UtilExtensionUserTests"

dotnet test -c %_C% WixToolsetTest.MsiE2E --nologo --no-build --filter "%_F%" -l "trx;LogFileName=%_L%\TestResults\WixToolsetTest.MsiE2E%_S%.trx" || exit /b

:LExit
@popd
@endlocal
