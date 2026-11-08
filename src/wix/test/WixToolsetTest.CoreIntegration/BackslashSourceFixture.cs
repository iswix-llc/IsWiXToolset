// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolsetTest.CoreIntegration
{
    using System;
    using System.IO;
    using System.Linq;
    using WixInternal.Core.TestPackage;
    using WixInternal.TestSupport;
    using WixToolset.Data.WindowsInstaller;
    using Xunit;

    public class BackslashSourceFixture
    {
        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void CanBuildSourcePathsWithWindowsSeparators()
        {
            var folder = TestData.Get("TestData", "BackslashSource");

            using (var fs = new DisposableFileSystem())
            {
                var baseFolder = fs.GetFolder();
                var intermediateFolder = Path.Combine(baseFolder, "obj");
                var msiPath = Path.Combine(baseFolder, "bin", "test.msi");

                var result = WixRunner.Execute(new[]
                {
                    "build",
                    Path.Combine(folder, "Package.wxs"),
                    "-intermediateFolder", intermediateFolder,
                    "-bindpath", folder,
                    "-bindpath", String.Concat("src=", folder),
                    "-o", msiPath,
                });

                result.AssertSuccess();

                var fileNames = ReadFileNames(msiPath);

                Assert.Equal("test.txt", fileNames["Nested"]);
                Assert.Equal("parent.txt", fileNames["Parent"]);
                Assert.Equal("bindpath.txt", fileNames["BindPath"]);
                Assert.Equal("test.txt", fileNames["InDirFile"]);
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void BackslashExcludeRemovesFileFromHarvest()
        {
            var folder = TestData.Get("TestData", "BackslashExclude");

            using (var fs = new DisposableFileSystem())
            {
                var baseFolder = fs.GetFolder();
                var intermediateFolder = Path.Combine(baseFolder, "obj");
                var msiPath = Path.Combine(baseFolder, "bin", "test.msi");

                var result = WixRunner.Execute(new[]
                {
                    "build",
                    Path.Combine(folder, "Package.wxs"),
                    "-intermediateFolder", intermediateFolder,
                    "-bindpath", folder,
                    "-o", msiPath,
                });

                // Fails with WIX0369 (duplicate component GUID) when the excluded file is harvested anyway.
                result.AssertSuccess();

                var fileNames = ReadFileNames(msiPath).Values
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, fileNames);
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void CanResolveIncludePathWithWindowsSeparators()
        {
            var folder = TestData.Get("TestData", "BackslashInclude");

            using (var fs = new DisposableFileSystem())
            {
                var baseFolder = fs.GetFolder();
                var intermediateFolder = Path.Combine(baseFolder, "obj");
                var msiPath = Path.Combine(baseFolder, "bin", "test.msi");

                var result = WixRunner.Execute(new[]
                {
                    "build",
                    Path.Combine(folder, "Package.wxs"),
                    "-intermediateFolder", intermediateFolder,
                    "-bindpath", folder,
                    "-o", msiPath,
                });

                // Fails with a missing include file when "inc\Common.wxi" is not normalized.
                result.AssertSuccess();

                var fileNames = ReadFileNames(msiPath);

                Assert.Equal("included.txt", fileNames["Included"]);
            }
        }

        // Reads the File table from the .wixpdb next to the msi so the tests do not need msi.dll and run on every platform.
        private static System.Collections.Generic.Dictionary<string, string> ReadFileNames(string msiPath)
        {
            var pdb = WindowsInstallerData.Load(Path.ChangeExtension(msiPath, ".wixpdb"));

            return pdb.Tables["File"].Rows.ToDictionary(
                row => row.FieldAsString(0),
                row =>
                {
                    // FileName is "short|long" when the name is not a valid 8.3 name.
                    var name = row.FieldAsString(2);
                    var bar = name.IndexOf('|');
                    return bar < 0 ? name : name.Substring(bar + 1);
                });
        }
    }
}
