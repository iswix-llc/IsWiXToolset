# IsWiX.Toolset

## About ISWIX LLC

[ISWIX LLC](https://www.iswix.com/) is the company behind IsWiX, a set of tools for building Windows Installer packages with the WiX Toolset. It is led by [Christopher Painter](https://stackoverflow.com/users/200936/christopher-painter), a trusted leader in the Windows Installer space for over 20 years. Follow along on the [ISWIX blog](https://blog.iswix.com/).

You do not have to take our word for it. These packages are built to be checked:

- **Open source.** Every package is built from the public source at [iswix-llc/IsWiX.Toolset](https://github.com/iswix-llc/IsWiX.Toolset), under the [Microsoft Reciprocal License (MS-RL)](https://github.com/iswix-llc/IsWiX.Toolset/blob/main/LICENSE.TXT).
- **Built from source, in the open.** The packages are compiled from that source in GitHub Actions rather than repackaged from someone else's binaries.
- **Comparable to upstream.** Assembly names and namespaces are unchanged, and the full history is public, so you can compare this fork with [wixtoolset/wix](https://github.com/wixtoolset/wix).

IsWiX.Toolset is a build of the [WiX Toolset](https://wixtoolset.org/) compiled from source and published as `IsWiX.WixToolSet.*` packages. It is a downstream fork of [wixtoolset/wix](https://github.com/wixtoolset/wix) and is **not** meant to replace it.

## Why this fork exists

- **Supply chain.** We had supply chain concerns that needed to be addressed. Building the toolset from source, and publishing the packages ourselves, lets us control what goes into the packages we depend on.
- **The OSMF.** The WiX Toolset is distributed under the [Open Source Maintenance Fee (OSMF)](https://opensourcemaintenancefee.org/) model. We encourage everyone to keep supporting the WiX Toolset maintainers. This fork also makes sure that people and organizations who cannot comply with the OSMF still have a way to use the toolset legally, under the [Microsoft Reciprocal License (MS-RL)](LICENSE.TXT) that the source code is licensed under.
- **A future for IsWiX.** IsWiX was built on top of the WiX Toolset. This fork gives IsWiX a toolset it can rely on going forward.

## What this is, and is not

- It is a build of the WiX Toolset from source. Package IDs use the `IsWiX.WixToolSet.` prefix; assembly names and namespaces are unchanged.
- It is not an official WiX Toolset release, and it is not affiliated with or endorsed by the WiX Toolset project.
- It is not a place for active development. We do not intend to do active development in this repository.

## Contributing and getting help

Interaction should stay with the primary project, with one exception described below. For questions, bug reports, feature requests and contributions, please use [wixtoolset/wix](https://github.com/wixtoolset/wix), the [WiX Toolset website](https://wixtoolset.org/) and the [FireGiant WiX documentation](https://www.firegiant.com/wixtoolset/).

If you engage with the WiX Toolset team, please be kind in your interactions. They are the people who build and maintain the toolset this fork depends on, and we encourage you to support them, either through [OSMF](https://opensourcemaintenancefee.org/) sponsorship or a [FireGiant](https://www.firegiant.com/wixtoolset/) support contract.

If you run into an issue, please reproduce it with an official WiX Toolset build before engaging with the WiX community. This fork is not an official release, so the WiX team can only act on problems that also occur in the official builds.

Finally, if you find a problem that is unique to these builds, meaning it does not happen with an official WiX Toolset build, please [file an issue here](https://github.com/iswix-llc/IsWiX.Toolset/issues) so we can look at it.

## License

Distributed under the [Microsoft Reciprocal License (MS-RL)](LICENSE.TXT).
