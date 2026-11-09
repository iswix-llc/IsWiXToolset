// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Burn.ExtensibilityServices
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using WixToolset.Core.Burn.Bundles;
    using WixToolset.Core.Burn.Interfaces;
    using WixToolset.Data.Symbols;
    using WixToolset.Dtf.Resources;

    internal class PayloadHarvester : IPayloadHarvester
    {
        private static readonly Version EmptyVersion = new Version(0, 0, 0, 0);

        /// <inheritdoc />
        public bool HarvestStandardInformation(WixBundlePayloadSymbol payload)
        {
            var filePath = payload.SourceFile?.Path;

            if (String.IsNullOrEmpty(filePath))
            {
                return false;
            }

            this.UpdatePayloadFileInformation(payload, filePath);

            this.UpdatePayloadVersionInformation(payload, filePath);

            return true;
        }

        private void UpdatePayloadFileInformation(WixBundlePayloadSymbol payload, string filePath)
        {
            var fileInfo = new FileInfo(filePath);

            if (null != fileInfo)
            {
                payload.FileSize = fileInfo.Length;

                payload.Hash = BundleHashAlgorithm.Hash(fileInfo);
            }
            else
            {
                payload.FileSize = 0;
            }
        }

        private static void UpdatePayloadVersionInformationFromResource(WixBundlePayloadSymbol payload, string filePath)
        {
            VersionResource versionResource = null;

            try
            {
                var resources = new ResourceCollection();
                resources.Find(filePath, ResourceType.Version);
                resources.Load(filePath);

                foreach (var resource in resources)
                {
                    versionResource = resource as VersionResource;

                    if (versionResource != null && resource.Data != null)
                    {
                        break;
                    }

                    versionResource = null;
                }
            }
            catch (Exception)
            {
                // Like FileVersionInfo, a file without readable version information just has none.
            }

            if (versionResource == null)
            {
                return;
            }

            string productName = null;
            string fileDescription = null;
            string productVersion = null;
            string fileVersion = null;

            foreach (var table in versionResource)
            {
                productName = table["ProductName"];
                fileDescription = table["FileDescription"];
                productVersion = table["ProductVersion"];
                fileVersion = table["FileVersion"];
                break;
            }

            var version = String.IsNullOrEmpty(productVersion) ? fileVersion : productVersion;

            if (String.IsNullOrEmpty(version))
            {
                var fixedVersion = versionResource.ProductVersion;

                if (fixedVersion != null && PayloadHarvester.EmptyVersion != fixedVersion)
                {
                    version = fixedVersion.ToString();
                }
            }

            payload.Description = fileDescription;
            payload.DisplayName = productName;
            payload.Version = version;
        }

        private void UpdatePayloadVersionInformation(WixBundlePayloadSymbol payload, string filePath)
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                // FileVersionInfo only understands managed assemblies off Windows; read the version resource directly.
                UpdatePayloadVersionInformationFromResource(payload, filePath);
                return;
            }

            var versionInfo = FileVersionInfo.GetVersionInfo(filePath);

            if (null != versionInfo)
            {
                var version = versionInfo.ProductVersion;

                if (String.IsNullOrEmpty(version))
                {
                    version = versionInfo.FileVersion;
                }

                if (String.IsNullOrEmpty(version))
                {
                    // Fallback to fixed version info block for the file.
                    var fixedVersion = new Version(versionInfo.ProductMajorPart, versionInfo.ProductMinorPart, versionInfo.ProductBuildPart, versionInfo.ProductPrivatePart);

                    if (PayloadHarvester.EmptyVersion != fixedVersion)
                    {
                        version = fixedVersion.ToString();
                    }
                }

                payload.Description = versionInfo.FileDescription;
                payload.DisplayName = versionInfo.ProductName;
                payload.Version = version;
            }
        }
    }
}
