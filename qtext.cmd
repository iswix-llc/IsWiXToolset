@echo off
rem Runs every extension test project (they are Microsoft.Testing.Platform executables, not VSTest).
rem Usage: qtext <run-name>      writes build\win-results\<run-name>-<project>.trx
setlocal
pushd %~dp0
set _N=%1
if "%_N%"=="" set _N=ext
for /d %%d in (build\*.wixext) do for %%f in ("%%d\Debug\net8.0\WixToolsetTest.*.dll") do (
  echo === %%~nf
  dotnet "%%f" --results-directory build\win-results --report-trx --report-trx-filename %_N%-%%~nf.trx
)
popd
endlocal
