# iswix - the IsWiX build of the WiX Toolset from the command-line

## About ISWIX LLC

[ISWIX LLC](https://www.iswix.com/) is the company behind IsWiX, a set of tools for building Windows Installer packages with the WiX Toolset. It is led by [Christopher Painter](https://stackoverflow.com/users/200936/christopher-painter), a trusted leader in the Windows Installer space for over 20 years. Follow along on the [ISWIX blog](https://blog.iswix.com/).

You do not have to take our word for it. These packages are built to be checked:

- **Open source.** Every package is built from the public source at [iswix-llc/IsWiX.Toolset](https://github.com/iswix-llc/IsWiX.Toolset), under the [Microsoft Reciprocal License (MS-RL)](https://github.com/iswix-llc/IsWiX.Toolset/blob/main/LICENSE.TXT).
- **Built from source, in the open.** The packages are compiled from that source in GitHub Actions rather than repackaged from someone else's binaries.
- **Comparable to upstream.** Assembly names and namespaces are unchanged, and the full history is public, so you can compare this fork with [wixtoolset/wix](https://github.com/wixtoolset/wix).

The `iswix` package provides the WiX Toolset as a .NET Tool, perfect for your command-line packaging pleasure (even if we recommend using the [IsWixToolset.Sdk][sdk]).

## About this build

This package is part of IsWiX, a build of the [WiX Toolset](https://wixtoolset.org/) compiled from source at [iswix-llc/IsWiX.Toolset](https://github.com/iswix-llc/IsWiX.Toolset) and distributed under the [Microsoft Reciprocal License (MS-RL)](https://github.com/iswix-llc/IsWiX.Toolset/blob/main/LICENSE.TXT). It is not an official WiX Toolset release and is not affiliated with or endorsed by the WiX Toolset project. Package IDs use the `IsWixToolset.` prefix; assembly names and namespaces are unchanged. Please direct questions, bug reports and contributions to the primary project at [wixtoolset/wix](https://github.com/wixtoolset/wix). If you find a problem that is unique to these builds, please [file an issue](https://github.com/iswix-llc/IsWiX.Toolset/issues).

## Before we begin

For most users, we recommend using the [IsWixToolset.Sdk][sdk] to build your installation packages instead of this command-line tool. The Sdk provides a better development experience than the command-line, especially as your project grows. The following is a quick example.

Example project file: `QuickStart.wixproj`
```
<Project Sdk="IsWixToolset.Sdk/7.0.1">
</Project>
```

Example source code: `QuickStart.wxs`
```
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Id="AcmeCorp.QuickStart" Name="QuickStart Example" Manufacturer="ACME Corp" Version="0.0.1">
    <File Source="example.txt" />
  </Package>
</Wix>
```

Build your MSI from the command-line:
```
dotnet build
```

## Getting started

If you still decide to use this command-line tool instead of the [IsWixToolset.Sdk][sdk], install the latest version with:

```
dotnet tool install --global iswix
```

Verify it was successfully installed with:

```
iswix --version
```

