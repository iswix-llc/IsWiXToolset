// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolsetTest.CoreNative
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using WixInternal.TestSupport;
    using WixToolset.Core.Native.Msm;
    using Xunit;
    using Lib = IsWiX.WindowsInstaller;

    // Tests of the managed merge module engine. The expected results were checked against mergemod.dll over a corpus of
    // real merge modules (see the MsmGroundTruth tool in the IsWiX.WindowsInstaller repository).
    public class MsmMergeFixture
    {
        private const string ModuleGuid = "11111111_2222_3333_4444_555555555555";

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void MergesTablesAndConnectsComponentsToTheFeature()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"));
                var module = CreateModule(Path.Combine(folder, "test.msm"));

                MergeInto(database, module, "F", "INSTALLDIR", connect: true);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.Equal(new[] { "F|Component1." + ModuleGuid }, Rows(db, "SELECT `Feature_`, `Component_` FROM `FeatureComponents`"));
                    Assert.Equal(new[] { "Component1." + ModuleGuid + "|MODDIR." + ModuleGuid }, Rows(db, "SELECT `Component`, `Directory_` FROM `Component`"));

                    // The module's top-level directory is moved under the redirect directory.
                    Assert.Equal(new[] { "MODDIR." + ModuleGuid + "|INSTALLDIR" }, Rows(db, "SELECT `Directory`, `Directory_Parent` FROM `Directory` WHERE `Directory` = 'MODDIR." + ModuleGuid + "'"));

                    // The module's own sequence and signature tables are not copied, but the signature is recorded.
                    Assert.False(db.Tables.Contains("ModuleInstallExecuteSequence"));
                    Assert.Equal(new[] { "TestModule." + ModuleGuid + "|0|1.0.0.0" }, Rows(db, "SELECT `ModuleID`, `Language`, `Version` FROM `ModuleSignature`"));
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void PlacesModuleActionsRelativeToTheBaseActions()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"));
                var module = CreateModule(Path.Combine(folder, "test.msm"));

                MergeInto(database, module, "F", "INSTALLDIR", connect: false);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.Equal(new[]
                    {
                        "WindowsFolder." + ModuleGuid + "||1",
                        "CostInitialize||800",
                        "Before." + ModuleGuid + "||801",
                        "CostFinalize||1000",
                        "After." + ModuleGuid + "|NOT REMOVE|1001",
                        "Second." + ModuleGuid + "||1002",
                    }, Sorted(Rows(db, "SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`"), 2));
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void GeneratesAnActionForADirectoryNamedAfterAStandardFolder()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"));
                var module = CreateModule(Path.Combine(folder, "test.msm"));

                MergeInto(database, module, "F", "INSTALLDIR", connect: false);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    var action = "WindowsFolder." + ModuleGuid;
                    Assert.Equal(new[] { action + "|51|" + action + "|[WindowsFolder]" }, Rows(db, "SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction` WHERE `Action` = '" + action + "'"));

                    // It runs before the actions that were in the table.
                    Assert.Equal(new[] { action + "|1" }, Rows(db, "SELECT `Action`, `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = '" + action + "'"));
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void RecordsAConflictingRowAndKeepsMerging()
        {
            // How mergemod.dll reports a conflicting row has not been captured yet; this documents what the engine does.
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"));
                var module = CreateModule(Path.Combine(folder, "test.msm"));

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.Direct))
                {
                    db.Execute("INSERT INTO `Property` (`Property`, `Value`) VALUES ('Shared." + ModuleGuid + "', 'base value')");
                }

                var merge = MsmInterop.GetManagedMsmMerge();
                merge.OpenDatabase(database);
                merge.OpenModule(module, 0);
                merge.MergeEx("F", "INSTALLDIR", null);
                var errors = merge.Errors;
                Assert.Equal(1, errors.Count);
                Assert.Equal(MsmErrorType.msmErrorTableMerge, errors[1].Type);
                Assert.Equal("Property", errors[1].DatabaseTable);
                merge.CloseModule();
                merge.CloseDatabase(true);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.Equal(new[] { "Shared." + ModuleGuid + "|base value" }, Rows(db, "SELECT `Property`, `Value` FROM `Property` WHERE `Property` = 'Shared." + ModuleGuid + "'"));
                    Assert.Equal(new[] { "Component1." + ModuleGuid }, Rows(db, "SELECT `Component` FROM `Component`"));
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void RejectsAModuleForAnotherPlatform()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"));
                var module = CreateModule(Path.Combine(folder, "test.msm"), "x64;0");

                var merge = MsmInterop.GetManagedMsmMerge();
                merge.OpenDatabase(database);
                merge.OpenModule(module, 0);
                Assert.ThrowsAny<System.Runtime.InteropServices.COMException>(() => merge.MergeEx("F", "INSTALLDIR", null));
                Assert.Equal(MsmErrorType.msmErrorPlatformMismatch, merge.Errors[1].Type);
                merge.CloseModule();
                merge.CloseDatabase(false);
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void RequiresAFeatureForAModuleThatNeedsOne()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"));
                var module = CreateModule(Path.Combine(folder, "test.msm"), nullFeature: true);

                var merge = MsmInterop.GetManagedMsmMerge();
                merge.OpenDatabase(database);
                merge.OpenModule(module, 0);

                // Like mergemod.dll, the merge succeeds and reports each row that needed a feature as an error.
                merge.MergeEx(String.Empty, String.Empty, null);
                var featureRequired = 0;
                for (var i = 1; i <= merge.Errors.Count; i++)
                {
                    if (merge.Errors[i].Type == MsmErrorType.msmErrorFeatureRequired)
                    {
                        featureRequired++;
                    }
                }

                Assert.True(featureRequired > 0);
                merge.CloseModule();
                merge.CloseDatabase(false);
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void OpensAModuleWithoutADatabase()
        {
            using (var fs = new DisposableFileSystem())
            {
                var module = CreateModule(Path.Combine(fs.GetFolder(true), "test.msm"));

                var merge = MsmInterop.GetManagedMsmMerge();
                merge.OpenModule(module, 0);
                merge.CloseModule();
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void ReportsAnActionWhoseBaseActionIsNotInTheModuleAsOrphaned()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"));
                var module = CreateModule(Path.Combine(folder, "test.msm"), defaultActions: false, customize: db =>
                {
                    db.Execute("INSERT INTO `ModuleInstallExecuteSequence` (`Action`, `BaseAction`, `After`) VALUES ('Orphan." + ModuleGuid + "', 'NoSuchAction', 1)");
                });

                var merge = MsmInterop.GetManagedMsmMerge();
                merge.OpenDatabase(database);
                merge.OpenModule(module, 0);
                merge.MergeEx("F", "INSTALLDIR", null);
                Assert.Equal(1, merge.Errors.Count);
                Assert.Equal(MsmErrorType.msmErrorResequenceMerge, merge.Errors[1].Type);
                Assert.Equal("ModuleInstallExecuteSequence", merge.Errors[1].ModuleTable);
                merge.CloseModule();
                merge.CloseDatabase(true);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.Empty(Rows(db, "SELECT `Action` FROM `InstallExecuteSequence` WHERE `Action` = 'Orphan." + ModuleGuid + "'"));
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void MovesTheActionOnATakenSequenceNumberUpByTheStep()
        {
            // 800 is divisible by 100, so the action there moves to 900 (and nothing is reported).
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"));
                var module = CreateModule(Path.Combine(folder, "test.msm"), defaultActions: false, customize: db =>
                {
                    db.Execute("INSERT INTO `ModuleInstallExecuteSequence` (`Action`, `Sequence`) VALUES ('Early." + ModuleGuid + "', 800)");
                });

                MergeInto(database, module, "F", "INSTALLDIR", connect: false);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.Equal(new[]
                    {
                        "Early." + ModuleGuid + "|800",
                        "CostInitialize|900",
                        "CostFinalize|1000",
                    }, Sorted(Rows(db, "SELECT `Action`, `Sequence` FROM `InstallExecuteSequence` WHERE `Sequence` > 1"), 1));
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void MovesEachActionTheMovedActionLandsOn()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"), customize: db =>
                {
                    db.Execute("INSERT INTO `InstallExecuteSequence` (`Action`, `Sequence`) VALUES ('Middle', 900)");
                });
                var module = CreateModule(Path.Combine(folder, "test.msm"), defaultActions: false, customize: db =>
                {
                    db.Execute("INSERT INTO `ModuleInstallExecuteSequence` (`Action`, `Sequence`) VALUES ('Early." + ModuleGuid + "', 800)");
                });

                MergeInto(database, module, "F", "INSTALLDIR", connect: false);

                // CostInitialize moves from 800 to 900, which moves Middle to 1000, which moves CostFinalize to 1100.
                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.Equal(new[]
                    {
                        "Early." + ModuleGuid + "|800",
                        "CostInitialize|900",
                        "Middle|1000",
                        "CostFinalize|1100",
                    }, Sorted(Rows(db, "SELECT `Action`, `Sequence` FROM `InstallExecuteSequence` WHERE `Sequence` > 1"), 1));
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void OffsetsFileSequencesByTheHighestSequenceInTheDatabase()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);

                // Media.LastSequence (10) is higher than any File.Sequence (3); the offset comes from the files.
                var database = CreateDatabase(Path.Combine(folder, "base.msi"), customize: db =>
                {
                    db.Execute("CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` INT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)");
                    db.Execute("CREATE TABLE `Media` (`DiskId` INT NOT NULL, `LastSequence` INT NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)");
                    db.Execute("INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('Old1', 'OldC', 'o1.txt', 5, 0, 1)");
                    db.Execute("INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('Old2', 'OldC', 'o2.txt', 5, 0, 2)");
                    db.Execute("INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('Old3', 'OldC', 'o3.txt', 5, 0, 3)");
                    db.Execute("INSERT INTO `Media` (`DiskId`, `LastSequence`, `Cabinet`) VALUES (1, 10, '#cab1.cab')");
                });
                var module = CreateModule(Path.Combine(folder, "test.msm"), defaultActions: false, customize: db =>
                {
                    db.Execute("CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` INT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)");
                    db.Execute("INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('F1." + ModuleGuid + "', 'Component1." + ModuleGuid + "', 'f1.txt', 10, 0, 1)");
                    db.Execute("INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('F2." + ModuleGuid + "', 'Component1." + ModuleGuid + "', 'f2.txt', 10, 0, 2)");
                });

                MergeInto(database, module, "F", "INSTALLDIR", connect: false);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.Equal(new[]
                    {
                        "Old1|1",
                        "Old2|2",
                        "Old3|3",
                        "F1." + ModuleGuid + "|4",
                        "F2." + ModuleGuid + "|5",
                    }, Sorted(Rows(db, "SELECT `File`, `Sequence` FROM `File`"), 1));
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void SkipsAModuleThatExcludesAModuleAlreadyInTheDatabase()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"), customize: db => AddSignature(db, "Other.1", 0, "1.5.0.0"));
                var module = CreateModule(Path.Combine(folder, "test.msm"), customize: db => AddExclusion(db, "Other.1", 0, null, null));

                var errors = MergeCollectingErrors(database, module);
                Assert.Equal(new[] { MsmErrorType.msmErrorExclusion }, errors);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.False(db.Tables.Contains("Component"));
                    Assert.False(db.Tables.Contains("ModuleComponents"));
                }
            }
        }

        [Theory]
        [Trait("Platform", "CrossPlatform")]
        [InlineData("1.5.0.0", true)]
        [InlineData("0.5.0.0", false)]
        [InlineData("3.0.0.0", false)]
        public void ExcludesOnlyVersionsInsideTheExcludedRange(string databaseVersion, bool excluded)
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"), customize: db => AddSignature(db, "Other.1", 0, databaseVersion));
                var module = CreateModule(Path.Combine(folder, "test.msm"), customize: db => AddExclusion(db, "Other.1", 0, "1.0.0.0", "2.0.0.0"));

                var errors = MergeCollectingErrors(database, module);
                Assert.Equal(excluded ? new[] { MsmErrorType.msmErrorExclusion } : new MsmErrorType[0], errors);
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void ReportsButStillMergesAModuleThatTheDatabaseExcludes()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"), customize: db =>
                {
                    db.Execute("CREATE TABLE `ModuleExclusion` (`ModuleID` CHAR(72) NOT NULL, `ModuleLanguage` INT NOT NULL, `ExcludedID` CHAR(72) NOT NULL, `ExcludedLanguage` INT NOT NULL, `ExcludedMinVersion` CHAR(32), `ExcludedMaxVersion` CHAR(32) PRIMARY KEY `ModuleID`, `ModuleLanguage`, `ExcludedID`, `ExcludedLanguage`)");
                    db.Execute("INSERT INTO `ModuleExclusion` (`ModuleID`, `ModuleLanguage`, `ExcludedID`, `ExcludedLanguage`) VALUES ('Resident.1', 0, 'TestModule." + ModuleGuid + "', 0)");
                });
                var module = CreateModule(Path.Combine(folder, "test.msm"));

                var errors = MergeCollectingErrors(database, module);
                Assert.Equal(new[] { MsmErrorType.msmErrorExclusion }, errors);

                using (var db = new Lib.Database(database, Lib.DatabaseOpenMode.ReadOnly))
                {
                    Assert.Equal(new[] { "Component1." + ModuleGuid }, Rows(db, "SELECT `Component` FROM `Component`"));
                }
            }
        }

        [Theory]
        [Trait("Platform", "CrossPlatform")]
        [InlineData("Intel;0", "x64;1033")]
        [InlineData(";0", "x64;1033")]
        [InlineData("Intel,x64;0", "x64;1033")]
        [InlineData("x64;0", "x64;1033")]
        [InlineData("Arm64;0", "Arm64;1033")]
        [InlineData("Intel;0", "Intel;1033")]
        public void MergesAModuleWhosePlatformFitsTheDatabase(string moduleTemplate, string databaseTemplate)
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"), databaseTemplate);
                var module = CreateModule(Path.Combine(folder, "test.msm"), moduleTemplate);

                MergeInto(database, module, "F", "INSTALLDIR", connect: false);
            }
        }

        [Theory]
        [Trait("Platform", "CrossPlatform")]
        [InlineData("x64;0", "Intel;1033")]
        [InlineData("x64;0", ";1033")]
        [InlineData("Intel64;0", "x64;1033")]
        [InlineData("Arm64;0", "x64;1033")]
        public void RejectsAModuleWhosePlatformDoesNotFitTheDatabase(string moduleTemplate, string databaseTemplate)
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var database = CreateDatabase(Path.Combine(folder, "base.msi"), databaseTemplate);
                var module = CreateModule(Path.Combine(folder, "test.msm"), moduleTemplate);

                var merge = MsmInterop.GetManagedMsmMerge();
                merge.OpenDatabase(database);
                merge.OpenModule(module, 0);
                Assert.ThrowsAny<System.Runtime.InteropServices.COMException>(() => merge.MergeEx("F", "INSTALLDIR", null));
                Assert.Equal(MsmErrorType.msmErrorPlatformMismatch, merge.Errors[1].Type);
                merge.CloseModule();
                merge.CloseDatabase(false);
            }
        }

        private static List<MsmErrorType> MergeCollectingErrors(string database, string module)
        {
            var merge = MsmInterop.GetManagedMsmMerge();
            merge.OpenDatabase(database);
            merge.OpenModule(module, 0);
            merge.MergeEx("F", "INSTALLDIR", null);
            var types = new List<MsmErrorType>();
            for (var i = 1; i <= merge.Errors.Count; i++)
            {
                types.Add(merge.Errors[i].Type);
            }

            merge.CloseModule();
            merge.CloseDatabase(true);
            return types;
        }

        private static void AddSignature(Lib.Database db, string id, int language, string version)
        {
            db.Execute("CREATE TABLE `ModuleSignature` (`ModuleID` CHAR(72) NOT NULL, `Language` INT NOT NULL, `Version` CHAR(72) NOT NULL PRIMARY KEY `ModuleID`, `Language`)");
            db.Execute("INSERT INTO `ModuleSignature` (`ModuleID`, `Language`, `Version`) VALUES ('" + id + "', " + language + ", '" + version + "')");
        }

        private static void AddExclusion(Lib.Database db, string excludedId, int excludedLanguage, string minimum, string maximum)
        {
            db.Execute("CREATE TABLE `ModuleExclusion` (`ModuleID` CHAR(72) NOT NULL, `ModuleLanguage` INT NOT NULL, `ExcludedID` CHAR(72) NOT NULL, `ExcludedLanguage` INT NOT NULL, `ExcludedMinVersion` CHAR(32), `ExcludedMaxVersion` CHAR(32) PRIMARY KEY `ModuleID`, `ModuleLanguage`, `ExcludedID`, `ExcludedLanguage`)");
            var columns = "`ModuleID`, `ModuleLanguage`, `ExcludedID`, `ExcludedLanguage`";
            var values = "'TestModule." + ModuleGuid + "', 0, '" + excludedId + "', " + excludedLanguage;
            if (minimum != null)
            {
                columns += ", `ExcludedMinVersion`";
                values += ", '" + minimum + "'";
            }

            if (maximum != null)
            {
                columns += ", `ExcludedMaxVersion`";
                values += ", '" + maximum + "'";
            }

            db.Execute("INSERT INTO `ModuleExclusion` (" + columns + ") VALUES (" + values + ")");
        }

        private static void MergeInto(string database, string module, string feature, string redirect, bool connect)
        {
            var merge = MsmInterop.GetManagedMsmMerge();
            merge.OpenDatabase(database);
            merge.OpenModule(module, 0);
            merge.MergeEx(feature, redirect, null);
            if (connect)
            {
                merge.Connect(feature);
            }

            Assert.Equal(0, merge.Errors.Count);
            merge.CloseModule();
            merge.CloseDatabase(true);
        }

        private static string CreateDatabase(string path, string template = "Intel;1033", Action<Lib.Database> customize = null)
        {
            using (var db = new Lib.Database(path, Lib.DatabaseOpenMode.Create))
            {
                db.Execute("CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)");
                db.Execute("CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)");
                db.Execute("CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` INT, `Level` INT NOT NULL, `Directory_` CHAR(72), `Attributes` INT NOT NULL PRIMARY KEY `Feature`)");
                db.Execute("INSERT INTO `Directory` (`Directory`, `DefaultDir`) VALUES ('TARGETDIR', 'SourceDir')");
                db.Execute("INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('INSTALLDIR', 'TARGETDIR', 'Install')");
                db.Execute("INSERT INTO `Feature` (`Feature`, `Title`, `Display`, `Level`, `Attributes`) VALUES ('F', 'F', 1, 1, 0)");

                foreach (var table in new[] { "InstallExecuteSequence", "InstallUISequence", "AdminExecuteSequence", "AdminUISequence", "AdvtExecuteSequence", "AdvtUISequence" })
                {
                    db.Execute("CREATE TABLE `" + table + "` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` INT PRIMARY KEY `Action`)");
                    db.Execute("INSERT INTO `" + table + "` (`Action`, `Sequence`) VALUES ('CostInitialize', 800)");
                    db.Execute("INSERT INTO `" + table + "` (`Action`, `Sequence`) VALUES ('CostFinalize', 1000)");
                }

                customize?.Invoke(db);
                db.Commit();
            }

            using (var summary = new Lib.SummaryInfo(path, true))
            {
                summary.Title = "Installation Database";
                summary.Template = template;
                summary.RevisionNumber = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
                summary.PageCount = 500;
                summary.WordCount = 0;
                summary.Persist();
            }

            return path;
        }

        // A module already in its modularized form, as the WiX compiler writes one.
        private static string CreateModule(string path, string template = "Intel;0", bool nullFeature = false, bool defaultActions = true, Action<Lib.Database> customize = null)
        {
            using (var db = new Lib.Database(path, Lib.DatabaseOpenMode.Create))
            {
                db.Execute("CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)");
                db.Execute("CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` INT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)");
                db.Execute("CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` INT NOT NULL, `Source` CHAR(72), `Target` CHAR(255) PRIMARY KEY `Action`)");
                db.Execute("CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)");
                db.Execute("CREATE TABLE `ModuleSignature` (`ModuleID` CHAR(72) NOT NULL, `Language` INT NOT NULL, `Version` CHAR(72) NOT NULL PRIMARY KEY `ModuleID`, `Language`)");
                db.Execute("CREATE TABLE `ModuleComponents` (`Component` CHAR(72) NOT NULL, `ModuleID` CHAR(72) NOT NULL, `Language` INT NOT NULL PRIMARY KEY `Component`, `ModuleID`, `Language`)");
                db.Execute("CREATE TABLE `ModuleInstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Sequence` INT, `BaseAction` CHAR(72), `After` INT, `Condition` CHAR(255) PRIMARY KEY `Action`)");

                db.Execute("INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('MODDIR." + ModuleGuid + "', 'TARGETDIR', 'Module')");
                db.Execute("INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('WindowsFolder." + ModuleGuid + "', 'TARGETDIR', '.')");
                db.Execute("INSERT INTO `Component` (`Component`, `ComponentId`, `Directory_`, `Attributes`) VALUES ('Component1." + ModuleGuid + "', '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}', 'MODDIR." + ModuleGuid + "', 0)");
                db.Execute("INSERT INTO `Property` (`Property`, `Value`) VALUES ('Shared." + ModuleGuid + "', 'module value')");
                if (nullFeature)
                {
                    // A row whose feature is the null GUID placeholder needs a feature to be merged.
                    db.Execute("CREATE TABLE `MsiAssembly` (`Component_` CHAR(72) NOT NULL, `Feature_` CHAR(38) NOT NULL, `File_Manifest` CHAR(72), `File_Application` CHAR(72), `Attributes` INT NOT NULL PRIMARY KEY `Component_`)");
                    db.Execute("INSERT INTO `MsiAssembly` (`Component_`, `Feature_`, `Attributes`) VALUES ('Component1." + ModuleGuid + "', '{00000000-0000-0000-0000-000000000000}', 0)");
                }

                db.Execute("INSERT INTO `ModuleSignature` (`ModuleID`, `Language`, `Version`) VALUES ('TestModule." + ModuleGuid + "', 0, '1.0.0.0')");
                db.Execute("INSERT INTO `ModuleComponents` (`Component`, `ModuleID`, `Language`) VALUES ('Component1." + ModuleGuid + "', 'TestModule." + ModuleGuid + "', 0)");

                if (defaultActions)
                {
                    // The module lists the base action with its sequence number; mergemod.dll reports a relative action whose base the module does not list as orphaned.
                    db.Execute("INSERT INTO `ModuleInstallExecuteSequence` (`Action`, `Sequence`) VALUES ('CostFinalize', 1000)");
                    db.Execute("INSERT INTO `ModuleInstallExecuteSequence` (`Action`, `BaseAction`, `After`, `Condition`) VALUES ('After." + ModuleGuid + "', 'CostFinalize', 1, 'NOT REMOVE')");
                    db.Execute("INSERT INTO `ModuleInstallExecuteSequence` (`Action`, `BaseAction`, `After`) VALUES ('Before." + ModuleGuid + "', 'CostFinalize', 0)");
                    db.Execute("INSERT INTO `ModuleInstallExecuteSequence` (`Action`, `BaseAction`, `After`) VALUES ('Second." + ModuleGuid + "', 'CostFinalize', 1)");
                    db.Execute("INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES ('After." + ModuleGuid + "', 51, 'SOMEPROP', 'value')");
                    db.Execute("INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES ('Before." + ModuleGuid + "', 51, 'OTHERPROP', 'value')");
                    db.Execute("INSERT INTO `CustomAction` (`Action`, `Type`, `Source`, `Target`) VALUES ('Second." + ModuleGuid + "', 51, 'THIRDPROP', 'value')");
                }

                customize?.Invoke(db);
                db.Commit();
            }

            using (var summary = new Lib.SummaryInfo(path, true))
            {
                summary.Title = "Merge Module";
                summary.Template = template;
                summary.RevisionNumber = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
                summary.PageCount = 500;
                summary.WordCount = 0;
                summary.Persist();
            }

            return path;
        }

        private static List<string> Rows(Lib.Database db, string query)
        {
            var rows = new List<string>();
            using (var view = db.OpenView(query))
            {
                view.Execute();
                for (var row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        var cells = new List<string>();
                        for (var i = 1; i <= row.FieldCount; i++)
                        {
                            cells.Add(row.IsNull(i) ? String.Empty : row.GetString(i));
                        }

                        rows.Add(String.Join("|", cells));
                    }
                }
            }

            return rows;
        }

        // Orders sequence rows by their sequence number (the last of the given number of trailing cells).
        private static string[] Sorted(List<string> rows, int sequenceCell)
        {
            rows.Sort((a, b) => Int32.Parse(a.Split('|')[sequenceCell]).CompareTo(Int32.Parse(b.Split('|')[sequenceCell])));
            return rows.ToArray();
        }
    }
}
