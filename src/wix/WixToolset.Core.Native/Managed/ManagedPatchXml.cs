// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Managed
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Xml.Linq;
    using Lib = IsWiX.WindowsInstaller;

    /// <summary>
    /// Builds the patch applicability XML of a patch package (what MsiExtractPatchXMLData returns) from the
    /// summary information of the patch and of the transforms stored in it.
    /// </summary>
    internal static class ManagedPatchXml
    {
        private static readonly XNamespace PatchNamespace = "http://www.microsoft.com/msi/patch_applicability.xsd";

        private const int ValidateLanguage = 0x1;
        private const int ValidateProduct = 0x2;
        private const int ValidateMajorVersion = 0x8;
        private const int ValidateMinorVersion = 0x10;
        private const int ValidateUpdateVersion = 0x20;
        private const int ValidateUpgradeCode = 0x800;

        internal static string Extract(string patchPath)
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), "wixpatchxml-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempFolder);

            try
            {
                var patchCode = String.Empty;
                var targets = new List<XElement>();

                using (var summary = new Lib.SummaryInfo(patchPath, false))
                {
                    var revision = summary.RevisionNumber ?? String.Empty;
                    patchCode = revision.Length >= 38 ? revision.Substring(0, 38) : revision;
                }

                using (var db = new Lib.Database(patchPath, Lib.DatabaseOpenMode.ReadOnly | Lib.DatabaseOpenMode.PatchFile))
                using (var view = db.OpenView("SELECT `Name`, `Data` FROM `_Storages`"))
                {
                    view.Execute();

                    var count = 0;
                    Lib.Record record;
                    while ((record = view.Fetch()) != null)
                    {
                        using (record)
                        {
                            var name = record.GetString(1);

                            // Names that start with # are the embedded transforms of the patch's own use, not target transforms.
                            if (String.IsNullOrEmpty(name) || name[0] == '#')
                            {
                                continue;
                            }

                            var transformPath = Path.Combine(tempFolder, (++count).ToString() + ".mst");
                            record.GetStream(2, transformPath);

                            using (var info = new Lib.SummaryInfo(transformPath, false))
                            {
                                targets.Add(CreateTargetProduct(info));
                            }
                        }
                    }
                }

                var root = new XElement(PatchNamespace + "MsiPatch",
                    new XAttribute("SchemaVersion", "1.0.0.0"),
                    new XAttribute("PatchGUID", patchCode),
                    targets);

                return new XDocument(root).ToString(SaveOptions.DisableFormatting);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempFolder, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static XElement CreateTargetProduct(Lib.SummaryInfo info)
        {
            // Revision number: <ref product code><ref version>;<updated product code><updated version>;<updated upgrade code>
            var parts = (info.RevisionNumber ?? String.Empty).Split(';');
            SplitCodeAndVersion(parts.Length > 0 ? parts[0] : String.Empty, out var targetCode, out var targetVersion);
            SplitCodeAndVersion(parts.Length > 1 ? parts[1] : String.Empty, out _, out var updatedVersion);
            var upgradeCode = parts.Length > 2 ? parts[2] : String.Empty;

            // Template: <platform>;<language>
            var template = (info.Template ?? String.Empty).Split(';');
            var platform = template.Length > 0 ? template[0] : String.Empty;
            var language = template.Length > 1 ? template[1] : String.Empty;

            // Character count: validation flags in the high word, error conditions in the low word.
            var validations = (info.CharacterCount >> 16) & 0xFFFF;

            var filter = "MajorMinorUpdate";
            if ((validations & ValidateUpdateVersion) == 0)
            {
                filter = (validations & ValidateMinorVersion) != 0 ? "MajorMinor" : (validations & ValidateMajorVersion) != 0 ? "Major" : "MajorMinorUpdate";
            }

            return new XElement(PatchNamespace + "TargetProduct",
                new XElement(PatchNamespace + "TargetProductCode", new XAttribute("Validate", Flag(validations, ValidateProduct)), targetCode),
                new XElement(PatchNamespace + "TargetVersion",
                    new XAttribute("Validate", Flag(validations, ValidateMajorVersion | ValidateMinorVersion | ValidateUpdateVersion)),
                    new XAttribute("ComparisonType", "Equal"),
                    new XAttribute("ComparisonFilter", filter),
                    targetVersion),
                new XElement(PatchNamespace + "UpdatedVersion", updatedVersion),
                new XElement(PatchNamespace + "UpgradeCode", new XAttribute("Validate", Flag(validations, ValidateUpgradeCode)), upgradeCode),
                new XElement(PatchNamespace + "Platform", platform),
                new XElement(PatchNamespace + "Language", new XAttribute("Validate", Flag(validations, ValidateLanguage)), language));
        }

        private static string Flag(int validations, int mask)
        {
            return (validations & mask) != 0 ? "true" : "false";
        }

        private static void SplitCodeAndVersion(string text, out string code, out string version)
        {
            // A GUID with braces is 38 characters; the version follows it.
            if (text.Length >= 38 && text[0] == '{')
            {
                code = text.Substring(0, 38);
                version = text.Substring(38);
            }
            else
            {
                code = String.Empty;
                version = text;
            }
        }
    }
}
