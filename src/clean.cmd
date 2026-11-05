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
if exist "%_NUGET_CACHE%\iswix.wixtoolset.bal.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.bal.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.bootstrapperapplications.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.bootstrapperapplications.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.bootstrapperextensionapi" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.bootstrapperextensionapi"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.bextutil" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.bextutil"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.burn" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.burn"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.complus.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.complus.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.converters" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.converters"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.core" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.core"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.core.burn" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.core.burn"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.core.extensioncache" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.core.extensioncache"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.core.native" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.core.native"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.core.windowsinstaller" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.core.windowsinstaller"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.data" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.data"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dependency.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dependency.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.directx.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.directx.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dtf.compression" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dtf.compression"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dtf.compression.cab" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dtf.compression.cab"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dtf.compression.zip" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dtf.compression.zip"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dtf.customaction" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dtf.customaction"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dtf.resources" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dtf.resources"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dtf.windowsinstaller" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dtf.windowsinstaller"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dtf.windowsinstaller.linq" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dtf.windowsinstaller.linq"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dtf.windowsinstaller.package" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dtf.windowsinstaller.package"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.dutil" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.dutil"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.extensibility" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.extensibility"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.firewall.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.firewall.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.http.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.http.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.iis.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.iis.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.bootstrapperapplicationapi" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.bootstrapperapplicationapi"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.msmq.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.msmq.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.netfx.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.netfx.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.powershell.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.powershell.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.sdk" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.sdk"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.sql.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.sql.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.util.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.util.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.ui.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.ui.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.versioning" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.versioning"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.visualstudio.wixext" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.visualstudio.wixext"
if exist "%_NUGET_CACHE%\iswix.wixtoolset.wcautil" rd /s/q "%_NUGET_CACHE%\iswix.wixtoolset.wcautil"
if exist "%_NUGET_CACHE%\iswix" rd /s/q "%_NUGET_CACHE%\iswix"

popd
endlocal
