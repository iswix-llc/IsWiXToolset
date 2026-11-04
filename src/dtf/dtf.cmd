@setlocal
@pushd %~dp0

@set _C=Debug
:parse_args
@if /i "%1"=="release" set _C=Release
@if /i "%1"=="inc" set _INC=1
@if /i "%1"=="clean" set _CLEAN=1
@if not "%1"=="" shift & goto parse_args

:: Clean

@if "%_INC%"=="" call :clean
@if NOT "%_CLEAN%"=="" goto :end

@echo Building dtf %_C%

msbuild -Restore SfxCA\sfxca_t.proj -p:Configuration=%_C% -tl -nologo -m -warnaserror -bl:..\..\build\logs\dtf_sfxca.binlog || exit /b

msbuild -Restore -t:Pack dtf.slnx -p:Configuration=%_C% -tl -nologo -m -warnaserror -bl:..\..\build\logs\dtf_build.binlog || exit /b

@goto :end

:clean
@rd /s/q "..\..\build\dtf" 2> nul
@del "..\..\build\artifacts\IsWixToolset.Dtf.*.nupkg" 2> nul
@rd /s/q "%USERPROFILE%\.nuget\packages\iswixtoolset.dtf.compression" 2> nul
@rd /s/q "%USERPROFILE%\.nuget\packages\iswixtoolset.dtf.compression.cab" 2> nul
@rd /s/q "%USERPROFILE%\.nuget\packages\iswixtoolset.dtf.compression.zip" 2> nul
@rd /s/q "%USERPROFILE%\.nuget\packages\iswixtoolset.dtf.customaction" 2> nul
@rd /s/q "%USERPROFILE%\.nuget\packages\iswixtoolset.dtf.resources" 2> nul
@rd /s/q "%USERPROFILE%\.nuget\packages\iswixtoolset.dtf.windowsinstaller" 2> nul
@rd /s/q "%USERPROFILE%\.nuget\packages\iswixtoolset.dtf.windowsinstaller.linq" 2> nul
@rd /s/q "%USERPROFILE%\.nuget\packages\iswixtoolset.dtf.windowsinstaller.package" 2> nul
@exit /b

:end
@popd
@endlocal
