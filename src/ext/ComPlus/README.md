# IsWiX.ComPlus.wixext - COM+ WiX Toolset Extension

This WiX Extension provides support for configuring COM+.

## About this build

This package is part of IsWiX, a build of the [WiX Toolset](https://wixtoolset.org/) compiled from source at [iswix-llc/IsWiX.Toolset](https://github.com/iswix-llc/IsWiX.Toolset) and distributed under the [Microsoft Reciprocal License (MS-RL)](https://github.com/iswix-llc/IsWiX.Toolset/blob/main/LICENSE.TXT). It is not an official WiX Toolset release and is not affiliated with or endorsed by the WiX Toolset project. Package IDs use the `IsWiX.` prefix; assembly names and namespaces are unchanged. Please direct questions, bug reports and contributions to the primary project at [wixtoolset/wix](https://github.com/wixtoolset/wix). If you find a problem that is unique to these builds, please [file an issue](https://github.com/iswix-llc/IsWiX.Toolset/issues).

## Getting started

Add the WiX Extension as a PackageReference to your .wixproj:

```
<Project Sdk="IsWiX.Sdk/7.0.1">
  <ItemGroup>
    <PackageReference Include="IsWiX.ComPlus.wixext" Version="7.0.1" />
  </ItemGroup>
</Project>
```

Then add the extension's namespace:

```
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:complus="http://wixtoolset.org/schemas/v4/wxs/complus">

  ..
</Wix>
```
