// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolsetTest.CoreIntegration
{
    using System;
    using System.IO;
    using System.Linq;
    using WixInternal.Core.TestPackage;
    using WixInternal.TestSupport;
    using Xunit;

    public class BackslashSourceFixture
    {
        [Fact]
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

                var rows = Query.QueryDatabase(msiPath, new[] { "File" });

                var fileNames = rows
                    .Select(row => row.Split(':', '\t'))
                    .ToDictionary(columns => columns[1], columns => columns[3]);

                Assert.Equal("test.txt", fileNames["Nested"]);
                Assert.Equal("parent.txt", fileNames["Parent"]);
                Assert.Equal("bindpath.txt", fileNames["BindPath"]);
                Assert.Equal("test.txt", fileNames["InDirFile"]);
            }
        }

        [Fact]
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

                var rows = Query.QueryDatabase(msiPath, new[] { "File" });

                var fileNames = rows
                    .Select(row => row.Split(':', '\t')[3])
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, fileNames);
            }
        }
    }
}
