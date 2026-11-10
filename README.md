# IsWiX.Toolset

## About ISWIX LLC

[ISWIX LLC](https://www.iswix.com/) is the company behind IsWiX, a set of tools for building Windows Installer packages with the WiX Toolset. It is led by [Christopher Painter](https://stackoverflow.com/users/200936/christopher-painter), a trusted leader in the Windows Installer space for over 20 years. Follow along on the [ISWIX blog](https://blog.iswix.com/).

You do not have to take our word for it. These packages are built to be checked:

- **Open source.** Every package is built from the public source at [iswix-llc/IsWiX.Toolset](https://github.com/iswix-llc/IsWiX.Toolset), under the [Microsoft Reciprocal License (MS-RL)](https://github.com/iswix-llc/IsWiX.Toolset/blob/main/LICENSE.TXT).
- **Built from source, in the open.** The packages are compiled from that source in GitHub Actions rather than repackaged from someone else's binaries.
- **Comparable to upstream.** Assembly names and namespaces are unchanged, and the full history is public, so you can compare this fork with [wixtoolset/wix](https://github.com/wixtoolset/wix).

IsWiX.Toolset is a build of the [WiX Toolset](https://wixtoolset.org/) compiled from source and published as `IsWiX.WixToolSet.*` packages. It is a downstream fork of [wixtoolset/wix](https://github.com/wixtoolset/wix) and is **not** meant to replace it.

## Why this fork exists

- **Supply chain.** We had supply chain concerns that needed to be addressed. Building the toolset from source, and publishing the packages ourselves, lets us ennsure the availability of the FOSS packages that everyone depends on.
- **The OSMF.**  The WiX Toolset has started being distributed under the [Open Source Maintenance Fee (OSMF)](https://opensourcemaintenancefee.org/) license. We preserve access to the WiX Toolset under the original [Microsoft Reciprocal License (MS-RL)](LICENSE.TXT) that the source code is licensed under.
- **A future for IsWiX.** IsWiX was built on top of the WiX Toolset. This fork gives IsWiX a new IsWiX ToolSet that you can rely on for years to come.
- 
## What this is, and is not

- It is a fork of the WiX Toolset from source. Package IDs use the `IsWiX.WixToolSet.` prefix; assembly names and namespaces are unchanged.
- It is not an official WiX Toolset release, and it is not affiliated with or endorsed by the WiX Toolset project.
- It is a place for active development and community. Please migrate [wixtoolset/issues](https://github.com/wixtoolset/issues/issues/) to here if you'd like them considered. 

## License

Distributed under the [Microsoft Reciprocal License (MS-RL)](LICENSE.TXT).
