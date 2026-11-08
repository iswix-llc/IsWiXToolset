// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolsetTest.CoreIntegration
{
    using System;
    using System.IO;
    using System.Linq;
    using WixInternal.Core.TestPackage;
    using WixInternal.TestSupport;
    using Xunit;

    public class EulaFixture
    {
        private static readonly string EulaFileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wix");
        private static readonly string EulaFilePath = Path.Combine(EulaFileFolder, "wix" + SomeVerInfo.Major + "-osmf-eula.txt");

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void BuildsWithoutEulaAcceptance()
        {
            CleanEulaFile();

            var folder = TestData.Get(@"TestData", "SingleFile");

            using (var fs = new DisposableFileSystem())
            {
                var baseFolder = fs.GetFolder();

                var result = WixRunner.Execute(
                [
                    "build",
                    Path.Combine(folder, "Package.wxs"),
                    Path.Combine(folder, "PackageComponents.wxs"),
                    "-loc", Path.Combine(folder, "Package.en-us.wxl"),
                    "-bindpath", Path.Combine(folder, "data"),
                    "-intermediateFolder", Path.Combine(baseFolder, "obj"),
                    "-o", Path.Combine(baseFolder, "bin", "test.msi"),
                ], out var messages, skipAcceptEula: true);

                Assert.Empty(messages.Where(m => m.Level == WixToolset.Data.MessageLevel.Error).Select(m => m.ToString()));
                Assert.Equal(0, result);
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void CanAcceptEula()
        {
            CleanEulaFile();

            try
            {
                var result = WixRunner.Execute(
                [
                    "eula", "accept",
                    "wix" + SomeVerInfo.Major,
                ], out var messages, skipAcceptEula: true);
                Assert.Equal(0, result);
                Assert.Empty(messages);

                Assert.True(File.Exists(EulaFilePath), "Failed to create EULA file");
            }
            finally
            {
                CleanEulaFile();
            }
        }

        private static void CleanEulaFile()
        {
            if (Directory.Exists(EulaFileFolder))
            {
                File.Delete(EulaFilePath);
                Directory.Delete(EulaFileFolder);
            }
        }
    }
}
