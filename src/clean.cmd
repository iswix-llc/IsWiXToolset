@echo off

setlocal
pushd %~dp0

set _NUGET_CACHE=%USERPROFILE%\.nuget\packages
if "%NUGET_PACKAGES%" NEQ "" set _NUGET_CACHE=%NUGET_PACKAGES%

echo Cleaning...

if exist ..\build rd /s/q ..\build
if exist ..\packages rd /s/q ..\packages

if exist Directory.Packages.props (del Directory.Packages.props)
if exist global.json (del global.json)

if exist ..\Directory.Packages.props (del ..\Directory.Packages.props)
if exist ..\global.json (del ..\global.json)

if exist "%_NUGET_CACHE%\wixinternal.basebuildtasks.sources" rd /s/q "%_NUGET_CACHE%\wixinternal.basebuildtasks.sources"
if exist "%_NUGET_CACHE%\wixinternal.testsupport" rd /s/q "%_NUGET_CACHE%\wixinternal.testsupport"
if exist "%_NUGET_CACHE%\wixinternal.mstestsupport" rd /s/q "%_NUGET_CACHE%\wixinternal.mstestsupport"
if exist "%_NUGET_CACHE%\wixinternal.core.testpackage" rd /s/q "%_NUGET_CACHE%\wixinternal.core.testpackage"
if exist "%_NUGET_CACHE%\wixinternal.core.mstestpackage" rd /s/q "%_NUGET_CACHE%\wixinternal.core.mstestpackage"
if exist "%_NUGET_CACHE%\iswixtoolset.bal.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.bal.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.bootstrapperapplications.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.bootstrapperapplications.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.bootstrapperextensionapi" rd /s/q "%_NUGET_CACHE%\iswixtoolset.bootstrapperextensionapi"
if exist "%_NUGET_CACHE%\iswixtoolset.bextutil" rd /s/q "%_NUGET_CACHE%\iswixtoolset.bextutil"
if exist "%_NUGET_CACHE%\iswixtoolset.burn" rd /s/q "%_NUGET_CACHE%\iswixtoolset.burn"
if exist "%_NUGET_CACHE%\iswixtoolset.complus.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.complus.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.converters" rd /s/q "%_NUGET_CACHE%\iswixtoolset.converters"
if exist "%_NUGET_CACHE%\iswixtoolset.core" rd /s/q "%_NUGET_CACHE%\iswixtoolset.core"
if exist "%_NUGET_CACHE%\iswixtoolset.core.burn" rd /s/q "%_NUGET_CACHE%\iswixtoolset.core.burn"
if exist "%_NUGET_CACHE%\iswixtoolset.core.extensioncache" rd /s/q "%_NUGET_CACHE%\iswixtoolset.core.extensioncache"
if exist "%_NUGET_CACHE%\iswixtoolset.core.native" rd /s/q "%_NUGET_CACHE%\iswixtoolset.core.native"
if exist "%_NUGET_CACHE%\iswixtoolset.core.windowsinstaller" rd /s/q "%_NUGET_CACHE%\iswixtoolset.core.windowsinstaller"
if exist "%_NUGET_CACHE%\iswixtoolset.data" rd /s/q "%_NUGET_CACHE%\iswixtoolset.data"
if exist "%_NUGET_CACHE%\iswixtoolset.dependency.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dependency.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.directx.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.directx.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.dtf.compression" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dtf.compression"
if exist "%_NUGET_CACHE%\iswixtoolset.dtf.compression.cab" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dtf.compression.cab"
if exist "%_NUGET_CACHE%\iswixtoolset.dtf.compression.zip" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dtf.compression.zip"
if exist "%_NUGET_CACHE%\iswixtoolset.dtf.customaction" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dtf.customaction"
if exist "%_NUGET_CACHE%\iswixtoolset.dtf.resources" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dtf.resources"
if exist "%_NUGET_CACHE%\iswixtoolset.dtf.windowsinstaller" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dtf.windowsinstaller"
if exist "%_NUGET_CACHE%\iswixtoolset.dtf.windowsinstaller.linq" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dtf.windowsinstaller.linq"
if exist "%_NUGET_CACHE%\iswixtoolset.dtf.windowsinstaller.package" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dtf.windowsinstaller.package"
if exist "%_NUGET_CACHE%\iswixtoolset.dutil" rd /s/q "%_NUGET_CACHE%\iswixtoolset.dutil"
if exist "%_NUGET_CACHE%\iswixtoolset.extensibility" rd /s/q "%_NUGET_CACHE%\iswixtoolset.extensibility"
if exist "%_NUGET_CACHE%\iswixtoolset.firewall.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.firewall.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.http.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.http.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.iis.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.iis.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.bootstrapperapplicationapi" rd /s/q "%_NUGET_CACHE%\iswixtoolset.bootstrapperapplicationapi"
if exist "%_NUGET_CACHE%\iswixtoolset.msmq.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.msmq.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.netfx.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.netfx.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.powershell.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.powershell.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.sdk" rd /s/q "%_NUGET_CACHE%\iswixtoolset.sdk"
if exist "%_NUGET_CACHE%\iswixtoolset.sql.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.sql.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.util.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.util.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.ui.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.ui.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.versioning" rd /s/q "%_NUGET_CACHE%\iswixtoolset.versioning"
if exist "%_NUGET_CACHE%\iswixtoolset.visualstudio.wixext" rd /s/q "%_NUGET_CACHE%\iswixtoolset.visualstudio.wixext"
if exist "%_NUGET_CACHE%\iswixtoolset.wcautil" rd /s/q "%_NUGET_CACHE%\iswixtoolset.wcautil"
if exist "%_NUGET_CACHE%\iswix" rd /s/q "%_NUGET_CACHE%\iswix"

popd
endlocal
