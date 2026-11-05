# IsWiX.WixToolSet.Bal.wixext - Bootstrapper Applications WiX Toolset Extension

## About ISWIX LLC

[ISWIX LLC](https://www.iswix.com/) is the company behind IsWiX, a set of tools for building Windows Installer packages with the WiX Toolset. It is led by [Christopher Painter](https://stackoverflow.com/users/200936/christopher-painter), a trusted leader in the Windows Installer space for over 20 years. Follow along on the [ISWIX blog](https://blog.iswix.com/).

You do not have to take our word for it. These packages are built to be checked:

- **Open source.** Every package is built from the public source at [iswix-llc/IsWiX.Toolset](https://github.com/iswix-llc/IsWiX.Toolset), under the [Microsoft Reciprocal License (MS-RL)](https://github.com/iswix-llc/IsWiX.Toolset/blob/main/LICENSE.TXT).
- **Built from source, in the open.** The packages are compiled from that source in GitHub Actions rather than repackaged from someone else's binaries.
- **Comparable to upstream.** Assembly names and namespaces are unchanged, and the full history is public, so you can compare this fork with [wixtoolset/wix](https://github.com/wixtoolset/wix).

This WiX Extension was renamed. Use `IsWiX.WixToolSet.BootstrapperApplications.wixext` instead.

- [WixStandardBootstrapperApplication](https://www.nuget.org/packages/IsWiX.WixToolSet.BootstrapperApplications.wixext)

## About this build

This package is part of IsWiX, a build of the [WiX Toolset](https://wixtoolset.org/) compiled from source at [iswix-llc/IsWiX.Toolset](https://github.com/iswix-llc/IsWiX.Toolset) and distributed under the [Microsoft Reciprocal License (MS-RL)](https://github.com/iswix-llc/IsWiX.Toolset/blob/main/LICENSE.TXT). It is not an official WiX Toolset release and is not affiliated with or endorsed by the WiX Toolset project. Package IDs use the `IsWiX.WixToolSet.` prefix; assembly names and namespaces are unchanged. Please direct questions, bug reports and contributions to the primary project at [wixtoolset/wix](https://github.com/wixtoolset/wix). If you find a problem that is unique to these builds, please [file an issue](https://github.com/iswix-llc/IsWiX.Toolset/issues).

## Getting started

Add the WiX Extension as a PackageReference to your .wixproj:

```
<Project Sdk="IsWiX.WixToolSet.Sdk/6.0.2">
  <ItemGroup>
    <PackageReference Include="IsWiX.WixToolSet.BootstrapperApplications.wixext" Version="6.0.2" />
  </ItemGroup>
</Project>
```

Then add the namespace and bootstrapper application of choice:

```
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:bal="http://wixtoolset.org/schemas/v4/wxs/bal">
  <Bundle BundleId="AcmeCorp.Example" Name="Example Bundle" Version="0.0.0.1" Manufacturer="ACME Corporation">
    <BootstrapperApplication>
      <bal:WixStandardBootstrapperApplication LicenseUrl="http://wixtoolset.org/about/license/" Theme="hyperlinkLicense" />
    </BootstrapperApplication>
  </Bundle>
</Wix>
```
