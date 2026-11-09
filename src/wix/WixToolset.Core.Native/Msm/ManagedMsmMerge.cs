// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Msm
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.RegularExpressions;
    using Lib = IsWiX.WindowsInstaller;

    /// <summary>
    /// A managed implementation of the merge module engine (IMsmMerge2, mergemod.dll), so merge modules can be
    /// merged on any platform. It follows what mergemod.dll does, as captured by running both over real modules:
    ///  - the module is modularized already, so identifiers are merged as they are;
    ///  - ModuleSubstitution is applied from the configuration (or the default values) before merging;
    ///  - the module's TARGETDIR children are moved under the redirect directory;
    ///  - the null GUID in a Feature_ column becomes the feature the module is merged into;
    ///  - Module*Sequence rows are placed in the matching sequence table, by number or relative to a base action;
    ///  - directories named after standard folders get a set-property custom action at sequence 1;
    ///  - every other table is merged with Database.Merge, and each component is connected to the feature.
    /// </summary>
    internal sealed class ManagedMsmMerge : IMsmMerge2, IDisposable
    {
        private const string MergeModuleCabinetStream = "MergeModule.CABinet";
        private const string NullGuid = "{00000000-0000-0000-0000-000000000000}";
        private const string ConflictTableName = "_MsmMergeConflicts";
        private const int EFail = unchecked((int)0x80004005);

        private static readonly string[] SequenceTableNames =
        {
            "AdminExecuteSequence", "AdminUISequence", "AdvtExecuteSequence", "AdvtUISequence", "InstallExecuteSequence", "InstallUISequence",
        };

        // The tables that get a generated custom action for each directory named after a standard folder.
        private static readonly string[] DirectoryActionSequenceTables =
        {
            "AdminUISequence", "AdminExecuteSequence", "AdvtExecuteSequence", "InstallUISequence", "InstallExecuteSequence",
        };

        private static readonly HashSet<string> StandardFolders = new HashSet<string>(StringComparer.Ordinal)
        {
            "AdminToolsFolder", "AppDataFolder", "CommonAppDataFolder", "CommonFiles64Folder", "CommonFilesFolder",
            "DesktopFolder", "FavoritesFolder", "FontsFolder", "LocalAppDataFolder", "MyPicturesFolder", "NetHoodFolder",
            "PersonalFolder", "PrintHoodFolder", "ProgramFiles64Folder", "ProgramFilesFolder", "ProgramMenuFolder",
            "RecentFolder", "SendToFolder", "StartMenuFolder", "StartupFolder", "System16Folder", "System64Folder",
            "SystemFolder", "TempFolder", "TemplateFolder", "WindowsFolder", "WindowsVolume",
        };

        private readonly List<MsmErrorItem> errors = new List<MsmErrorItem>();

        private Lib.Database database;
        private string databasePlatform;
        private StreamWriter log;

        private Lib.Database module;
        private string modulePath;
        private string moduleId;

        public IMsmErrors Errors => new MsmErrorList(this.errors);

        public object Dependencies => null;

        public object ConfigurableItems => null;

        public IMsmStrings ModuleFiles
        {
            get
            {
                this.RequireModule();
                return new MsmStringList(this.ReadStrings(this.module, "SELECT `File` FROM `File`"));
            }
        }

        public void OpenLog(string fileName)
        {
            this.CloseLog();
            this.log = new StreamWriter(fileName, false, new UTF8Encoding(false));
        }

        public void CloseLog()
        {
            this.log?.Dispose();
            this.log = null;
        }

        public void Log(string message)
        {
            this.WriteLog(message);
        }

        public void OpenDatabase(string path)
        {
            if (this.database != null)
            {
                throw Fail("A database is already open.");
            }

            try
            {
                this.databasePlatform = ReadTemplate(path);
                this.database = new Lib.Database(path, Lib.DatabaseOpenMode.Transact);
            }
            catch (Exception e) when (e is Lib.InstallerException || e is IOException)
            {
                throw Fail("Cannot open the database '" + path + "': " + e.Message);
            }

            this.WriteLog("Opened MSI Database: " + path);
        }

        public void CloseDatabase(bool commit)
        {
            if (this.database == null)
            {
                return;
            }

            if (commit)
            {
                this.database.Commit();
                this.WriteLog("Committed changes to MSI Database.");
            }

            this.database.Dispose();
            this.database = null;
            this.WriteLog("Closed MSI Database.");
        }

        public void OpenModule(string fileName, short language)
        {
            // No database is needed to open a module: extracting its files opens only the module.
            if (this.module != null)
            {
                throw Fail("A merge module is already open.");
            }

            this.errors.Clear();

            string template;
            try
            {
                template = ReadTemplate(fileName);
            }
            catch (Exception e) when (e is Lib.InstallerException || e is IOException)
            {
                throw Fail("Cannot open the merge module '" + fileName + "': " + e.Message);
            }

            try
            {
                this.module = new Lib.Database(fileName, Lib.DatabaseOpenMode.ReadOnly);
            }
            catch (Lib.InstallerException e)
            {
                throw Fail("Cannot open the merge module '" + fileName + "': " + e.Message);
            }

            // A database without a ModuleSignature table is not a merge module (mergemod.dll aborts).
            if (!this.module.Tables.Contains("ModuleSignature"))
            {
                this.module.Dispose();
                this.module = null;
                throw new COMException("The file '" + fileName + "' is not a merge module.", unchecked((int)0x80004004));
            }

            // The first language is the one the module is built in. Others are language transforms inside the module,
            // which are not applied here. mergemod.dll takes any language the module has no transform for.
            List<int> languages = ParseLanguages(template);
            if (language != 0 && languages.Count > 0 && languages[0] != language && this.HasLanguageTransform(language))
            {
                this.module.Dispose();
                this.module = null;
                this.AddError(MsmErrorType.msmErrorLanguageFailed, language: language);
                throw Fail("Applying the language transform for language " + language + " is not supported.");
            }

            this.modulePath = fileName;
            this.moduleId = this.module.Tables.Contains("ModuleSignature")
                ? this.ReadStrings(this.module, "SELECT `ModuleID` FROM `ModuleSignature`").FirstOrDefault()
                : null;

            this.WriteLog("Opened Merge Module: " + fileName);
        }

        private bool HasLanguageTransform(int language)
        {
            try
            {
                string name = language.ToString(CultureInfo.InvariantCulture);
                return this.ReadStrings(this.module, "SELECT `Name` FROM `_Storages`").Contains(name);
            }
            catch (Lib.InstallerException)
            {
                return false;
            }
        }

        public void CloseModule()
        {
            if (this.module == null)
            {
                return;
            }

            this.module.Dispose();
            this.module = null;
            this.modulePath = null;
            this.moduleId = null;
            this.WriteLog("Closed Merge Module.");
        }

        public void Merge(string feature, string redirectDir)
        {
            this.MergeEx(feature, redirectDir, null);
        }

        public void MergeEx(string feature, string redirectDir, IMsmConfigureModule configuration)
        {
            this.RequireDatabase();
            this.RequireModule();

            string workingPath = Path.Combine(Path.GetTempPath(), "msm-" + Guid.NewGuid().ToString("N") + ".msm");
            try
            {
                File.Copy(this.modulePath, workingPath, true);

                using (Lib.Database working = new Lib.Database(workingPath, Lib.DatabaseOpenMode.Direct))
                {
                    this.MergeWorkingCopy(working, feature, redirectDir, configuration);
                }
            }
            catch (Lib.InstallerException e)
            {
                throw Fail("The merge failed: " + e.Message + " (error " + e.ErrorCode + ") " + FirstFrames(e));
            }
            catch (Exception e) when (!(e is COMException))
            {
                throw Fail("The merge failed: " + e.GetType().Name + ": " + e.Message + " " + FirstFrames(e));
            }
            finally
            {
                try
                {
                    File.Delete(workingPath);
                }
                catch (IOException)
                {
                }
            }
        }

        public void Connect(string feature)
        {
            this.RequireDatabase();
            this.RequireModule();

            try
            {
                this.ConnectComponents(feature);
            }
            catch (Lib.InstallerException e)
            {
                throw Fail("Connecting the module to feature '" + feature + "' failed: " + e.Message);
            }
        }

        public void ExtractCAB(string fileName)
        {
            this.RequireModule();

            try
            {
                using (Lib.View view = this.module.OpenView("SELECT `Data` FROM `_Streams` WHERE `Name` = ?"))
                {
                    view.Execute(new Lib.Record(MergeModuleCabinetStream));

                    using (Lib.Record record = view.Fetch())
                    {
                        if (record == null)
                        {
                            throw Fail("The merge module has no embedded cabinet.");
                        }

                        string directory = Path.GetDirectoryName(Path.GetFullPath(fileName));
                        Directory.CreateDirectory(directory);
                        File.Delete(fileName);
                        record.GetStream(1, fileName);
                    }
                }
            }
            catch (Lib.InstallerException e)
            {
                throw Fail("Cannot read the merge module's cabinet: " + e.Message);
            }
        }

        public void ExtractFiles(string path)
        {
            string cabinetPath = Path.Combine(Path.GetTempPath(), "msm-" + Guid.NewGuid().ToString("N") + ".cab");
            try
            {
                this.ExtractCAB(cabinetPath);
                Directory.CreateDirectory(path);

                try
                {
                    new Cabinet(cabinetPath).Extract(path);
                }
                catch (Exception e) when (!(e is COMException))
                {
                    throw Fail("Cannot extract the merge module's files: " + e.Message);
                }
            }
            finally
            {
                File.Delete(cabinetPath);
            }
        }

        public void ExtractFilesEx(string path, bool longFileNames, ref IntPtr filePaths)
        {
            this.ExtractFiles(path);
        }

        public void CreateSourceImage(string path, bool longFileNames, ref IntPtr filePaths)
        {
            this.ExtractFiles(path);
        }

        public void Dispose()
        {
            this.CloseModule();
            this.CloseDatabase(false);
            this.CloseLog();
        }

        private void MergeWorkingCopy(Lib.Database working, string feature, string redirectDir, IMsmConfigureModule configuration)
        {
            this.CheckPlatform();
            if (this.CheckExclusions())
            {
                return;
            }

            List<string> directories = working.Tables.Contains("Directory")
                ? this.ReadStrings(working, "SELECT `Directory` FROM `Directory`")
                : new List<string>();

            this.ApplySubstitutions(working, configuration);

            if (!String.IsNullOrEmpty(redirectDir) && working.Tables.Contains("Directory"))
            {
                working.Execute("UPDATE `Directory` SET `Directory_Parent` = ? WHERE `Directory_Parent` = 'TARGETDIR'", new Lib.Record(redirectDir));
            }

            this.ApplyFeature(working, feature);

            List<SequenceRow> sequenceRows = this.ReadSequenceRows(working);
            this.DropModuleOnlyTables(working);

            if (this.HasSignatureConflict(working))
            {
                this.WriteLog(">> Error: Failed to merge Merge Module due to a conflicting Merge Module.");
                return;
            }

            this.OffsetFileSequences(working);

            this.WriteLog("Merging module tables.");
            if (!this.MergeTables(working))
            {
                return;
            }

            Dictionary<string, int> lowestSequences = this.ReadLowestSequences();
            this.MergeSequences(sequenceRows);
            this.AddDirectoryActions(directories, lowestSequences);

            this.ConnectComponents(feature);
        }

        private bool HasSignatureConflict(Lib.Database working)
        {
            if (!working.Tables.Contains("ModuleSignature") || !this.database.Tables.Contains("ModuleSignature"))
            {
                return false;
            }

            List<Signature> existing = this.ReadSignatures(this.database);
            foreach (Signature signature in this.ReadSignatures(working))
            {
                if (existing.Any(e => e.Id == signature.Id && e.Language == signature.Language && e.Version != signature.Version))
                {
                    string[] keys = new[] { signature.Id, signature.Language.ToString(CultureInfo.InvariantCulture) };
                    this.AddError(MsmErrorType.msmErrorTableMerge, databaseTable: "ModuleSignature", databaseKeys: keys, moduleTable: "ModuleSignature", moduleKeys: keys);
                    return true;
                }
            }

            return false;
        }

        /// <summary>The module's files come after the files already in the database: their sequence numbers are moved up by the highest one there.</summary>
        private void OffsetFileSequences(Lib.Database working)
        {
            if (!working.Tables.Contains("File") || !this.database.Tables.Contains("File"))
            {
                return;
            }

            int highest = 0;
            using (Lib.View view = this.database.OpenView("SELECT `Sequence` FROM `File`"))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        if (!row.IsNull(1))
                        {
                            highest = Math.Max(highest, row.GetInteger(1));
                        }
                    }
                }
            }

            if (highest <= 0)
            {
                return;
            }

            var files = new List<KeyValuePair<string, int>>();
            using (Lib.View view = working.OpenView("SELECT `File`, `Sequence` FROM `File`"))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        if (!row.IsNull(2))
                        {
                            files.Add(new KeyValuePair<string, int>(row.GetString(1), row.GetInteger(2)));
                        }
                    }
                }
            }

            foreach (KeyValuePair<string, int> file in files)
            {
                this.WriteLog("     * Changing " + file.Key + "'s Sequence Column from " + file.Value + " to " + (file.Value + highest) + ".");
                working.Execute("UPDATE `File` SET `Sequence` = ? WHERE `File` = ?", new Lib.Record(file.Value + highest, file.Key));
            }
        }

        private void CheckPlatform()
        {
            string template = ReadTemplate(this.modulePath);
            if (!PlatformsOverlap(template, this.databasePlatform))
            {
                // mergemod.dll reports this error with no path and language -1.
                this.AddError(MsmErrorType.msmErrorPlatformMismatch, language: -1);
                throw Fail("The merge module's platform ('" + template + "') does not match the database's ('" + this.databasePlatform + "').");
            }
        }

        /// <summary>Returns true when the module is excluded by a module already merged, and so is not merged.</summary>
        private bool CheckExclusions()
        {
            if (!this.module.Tables.Contains("ModuleSignature"))
            {
                return false;
            }

            List<Signature> moduleSignatures = this.ReadSignatures(this.module);
            List<Signature> databaseSignatures = this.database.Tables.Contains("ModuleSignature") ? this.ReadSignatures(this.database) : new List<Signature>();

            // mergemod.dll skips a module that excludes one already in the database, with the excluded module as the
            // database key. A module the database excludes is merged all the same, with its own signature as the error.
            bool skip = false;
            if (this.module.Tables.Contains("ModuleExclusion"))
            {
                skip = this.FindExclusions(this.module, databaseSignatures, true);
                if (skip)
                {
                    this.WriteLog(">> Error: Merge Module `" + this.modulePath + "` is excluded by another Merge Module.");
                }
            }

            if (this.database.Tables.Contains("ModuleExclusion"))
            {
                this.FindExclusions(this.database, moduleSignatures, false);
            }

            return skip;
        }

        private bool FindExclusions(Lib.Database source, List<Signature> candidates, bool databaseKeys)
        {
            bool found = false;
            using (Lib.View view = source.OpenView("SELECT `ExcludedID`, `ExcludedLanguage`, `ExcludedMinVersion`, `ExcludedMaxVersion` FROM `ModuleExclusion`"))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        string id = row.GetString(1);
                        int language = row.IsNull(2) ? 0 : row.GetInteger(2);
                        string minimum = row.IsNull(3) ? null : row.GetString(3);
                        string maximum = row.IsNull(4) ? null : row.GetString(4);

                        foreach (Signature candidate in candidates)
                        {
                            if (!String.Equals(candidate.Id, id, StringComparison.Ordinal) || !LanguageExcluded(language, candidate.Language))
                            {
                                continue;
                            }

                            if ((minimum != null && CompareVersions(candidate.Version, minimum) < 0) || (maximum != null && CompareVersions(candidate.Version, maximum) > 0))
                            {
                                continue;
                            }

                            found = true;
                            string[] keys = new[] { candidate.Id, candidate.Language.ToString(CultureInfo.InvariantCulture), candidate.Version };
                            if (databaseKeys)
                            {
                                this.AddError(MsmErrorType.msmErrorExclusion, databaseKeys: keys);
                            }
                            else
                            {
                                this.AddError(MsmErrorType.msmErrorExclusion, moduleKeys: keys);
                            }
                        }
                    }
                }
            }

            return found;
        }

        private void ApplySubstitutions(Lib.Database working, IMsmConfigureModule configuration)
        {
            // mergemod.dll leaves the module as it is when no configuration callback is given.
            if (configuration == null || !working.Tables.Contains("ModuleSubstitution"))
            {
                return;
            }

            Dictionary<string, ConfigurationItem> items = this.ReadConfiguration(working);
            var substitutions = new List<string[]>();
            using (Lib.View view = working.OpenView("SELECT `Table`, `Row`, `Column`, `Value` FROM `ModuleSubstitution`"))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        substitutions.Add(new[] { row.GetString(1), row.GetString(2), row.GetString(3), row.IsNull(4) ? null : row.GetString(4) });
                    }
                }
            }

            foreach (string[] substitution in substitutions)
            {
                string table = substitution[0];
                string column = substitution[2];
                string value = this.Expand(substitution[3], items, configuration, table, substitution[1]);

                if (!working.Tables.Contains(table) || !working.Tables[table].Columns.Contains(column))
                {
                    this.AddError(MsmErrorType.msmErrorBadSubstitutionType, moduleTable: table, moduleKeys: new[] { substitution[1] });
                    throw Fail("The substitution refers to " + table + "." + column + ", which the module does not have.");
                }

                Lib.TableInfo info = working.Tables[table];
                Lib.ColumnInfo columnInfo = info.Columns[column];
                if (String.IsNullOrEmpty(value) && columnInfo.IsRequired)
                {
                    this.AddError(MsmErrorType.msmErrorBadNullSubstitution, moduleTable: table, moduleKeys: new[] { substitution[1] });
                    throw Fail("The substitution would set the required column " + table + "." + column + " to null.");
                }

                object cell = String.IsNullOrEmpty(value) ? null : (columnInfo.Type == typeof(int) || columnInfo.Type == typeof(short) ? (object)Int32.Parse(value, CultureInfo.InvariantCulture) : value);

                string[] keyValues = substitution[1].Split('\t');
                var whereClause = new StringBuilder();
                var parameters = new List<object> { cell };
                for (int i = 0; i < info.PrimaryKeys.Count && i < keyValues.Length; i++)
                {
                    whereClause.Append(i == 0 ? " WHERE " : " AND ").Append('`').Append(info.PrimaryKeys[i]).Append("` = ?");
                    Lib.ColumnInfo keyColumn = info.Columns[info.PrimaryKeys[i]];
                    parameters.Add(keyColumn.Type == typeof(int) || keyColumn.Type == typeof(short) ? (object)Int32.Parse(keyValues[i], CultureInfo.InvariantCulture) : keyValues[i]);
                }

                working.Execute("UPDATE `" + table + "` SET `" + column + "` = ?" + whereClause, new Lib.Record(parameters.ToArray()));
            }
        }

        private string Expand(string template, Dictionary<string, ConfigurationItem> items, IMsmConfigureModule configuration, string table, string row)
        {
            if (template == null)
            {
                return null;
            }

            return Regex.Replace(template, @"\[=([^\]]+)\]", match =>
            {
                string name = match.Groups[1].Value;
                if (!items.TryGetValue(name, out ConfigurationItem item))
                {
                    throw Fail("The module has no configuration item '" + name + "'.");
                }

                if (configuration != null)
                {
                    if (item.Format == 2 || item.Format == 3)
                    {
                        if (configuration.ProvideIntegerData(name, out int number) == 0)
                        {
                            return number.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    else if (configuration.ProvideTextData(name, out string text) == 0)
                    {
                        return text ?? String.Empty;
                    }
                }

                return item.DefaultValue ?? String.Empty;
            });
        }

        private Dictionary<string, ConfigurationItem> ReadConfiguration(Lib.Database working)
        {
            var items = new Dictionary<string, ConfigurationItem>(StringComparer.Ordinal);
            if (!working.Tables.Contains("ModuleConfiguration"))
            {
                return items;
            }

            using (Lib.View view = working.OpenView("SELECT `Name`, `Format`, `DefaultValue` FROM `ModuleConfiguration`"))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        items[row.GetString(1)] = new ConfigurationItem { Format = row.GetInteger(2), DefaultValue = row.IsNull(3) ? null : row.GetString(3) };
                    }
                }
            }

            return items;
        }

        private void ApplyFeature(Lib.Database working, string feature)
        {
            foreach (Lib.TableInfo table in working.Tables.ToList())
            {
                if (table.Name == "FeatureComponents" || table.Name == "Feature" || !table.Columns.Contains("Feature_"))
                {
                    continue;
                }

                List<string> keys = this.ReadStrings(working, "SELECT `" + table.PrimaryKeys[0] + "` FROM `" + table.Name + "` WHERE `Feature_` = '" + NullGuid + "'");
                if (keys.Count == 0)
                {
                    continue;
                }

                if (String.IsNullOrEmpty(feature))
                {
                    // mergemod.dll reports each row, leaves it out and merges the rest.
                    foreach (string key in keys)
                    {
                        this.WriteLog(">> Error: Failed to merge Row: " + key + " into Table: " + table.Name + ". Feature required.");
                        this.AddError(MsmErrorType.msmErrorFeatureRequired, databaseTable: table.Name, databaseKeys: new[] { key }, moduleTable: table.Name, moduleKeys: new[] { key });
                    }

                    working.Execute("DELETE FROM `" + table.Name + "` WHERE `Feature_` = '" + NullGuid + "'");
                    continue;
                }

                working.Execute("UPDATE `" + table.Name + "` SET `Feature_` = ? WHERE `Feature_` = '" + NullGuid + "'", new Lib.Record(feature));
            }

            // A property whose value is the placeholder (a module records its owning feature this way) gets the feature too.
            if (!String.IsNullOrEmpty(feature) && working.Tables.Contains("Property"))
            {
                working.Execute("UPDATE `Property` SET `Value` = ? WHERE `Value` = '" + NullGuid + "'", new Lib.Record(feature));
            }
        }

        private List<SequenceRow> ReadSequenceRows(Lib.Database working)
        {
            var rows = new List<SequenceRow>();
            foreach (string name in SequenceTableNames)
            {
                string moduleTableName = "Module" + name;
                if (!working.Tables.Contains(moduleTableName))
                {
                    continue;
                }

                using (Lib.View view = working.OpenView("SELECT `Action`, `Sequence`, `BaseAction`, `After`, `Condition` FROM `" + moduleTableName + "`"))
                {
                    view.Execute();
                    for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                    {
                        using (row)
                        {
                            rows.Add(new SequenceRow
                            {
                                Table = name,
                                Action = row.GetString(1),
                                Sequence = row.IsNull(2) ? (int?)null : row.GetInteger(2),
                                BaseAction = row.IsNull(3) ? null : row.GetString(3),
                                After = !row.IsNull(4) && row.GetInteger(4) != 0,
                                Condition = row.IsNull(5) ? null : row.GetString(5),
                            });
                        }
                    }
                }
            }

            return rows;
        }

        private void DropModuleOnlyTables(Lib.Database working)
        {
            var drop = new HashSet<string>(StringComparer.Ordinal) { "ModuleIgnoreTable", "ModuleSubstitution", "ModuleConfiguration" };
            foreach (string name in SequenceTableNames)
            {
                drop.Add("Module" + name);

                // The standard sequence tables a module may carry are not merged either; only its Module*Sequence rows are.
                drop.Add(name);
            }

            if (working.Tables.Contains("ModuleIgnoreTable"))
            {
                this.WriteLog("Processing ModuleIgnoreTable table.");
                foreach (string name in this.ReadStrings(working, "SELECT `Table` FROM `ModuleIgnoreTable`"))
                {
                    drop.Add(name);
                }
            }

            foreach (string name in drop)
            {
                if (working.Tables.Contains(name))
                {
                    working.Execute("DROP TABLE `" + name + "`");
                }
            }
        }

        /// <summary>Merges the working copy into the database. Returns false when the merge cannot continue.</summary>
        private bool MergeTables(Lib.Database working)
        {
            try
            {
                this.database.Merge(working, ConflictTableName);
                return true;
            }
            catch (Lib.MergeException e)
            {
                IList<string> tables;
                try
                {
                    tables = e.ConflictTables;
                }
                catch (ArgumentNullException)
                {
                    // A schema difference carries no list of conflicting tables.
                    tables = null;
                }

                if (tables == null || tables.Count == 0)
                {
                    this.AddError(MsmErrorType.msmErrorTableMerge);
                    throw Fail("The module's tables do not match the database's tables. " + this.DescribeSchemaDifferences(working));
                }

                foreach (string table in tables)
                {
                    this.ReportConflicts(working, table);
                }

                // The rows that did not conflict are merged, as they are by mergemod.dll.
                return true;
            }
            finally
            {
                if (this.database.Tables.Contains(ConflictTableName))
                {
                    this.database.Execute("DROP TABLE `" + ConflictTableName + "`");
                }
            }
        }

        private string DescribeSchemaDifferences(Lib.Database working)
        {
            var differences = new List<string>();
            foreach (Lib.TableInfo table in working.Tables)
            {
                if (!this.database.Tables.Contains(table.Name))
                {
                    continue;
                }

                string left = DescribeColumns(table);
                string right = DescribeColumns(this.database.Tables[table.Name]);
                if (left != right)
                {
                    differences.Add(table.Name + ": module (" + left + ") database (" + right + ")");
                }
            }

            return String.Join("; ", differences);
        }

        private static string DescribeColumns(Lib.TableInfo table)
        {
            return String.Join(", ", table.Columns.Select(c => c.Name + " " + c.Type.Name + (c.IsRequired ? " NOT NULL" : String.Empty) + (table.PrimaryKeys.Contains(c.Name) ? " PK" : String.Empty)));
        }

        private void ReportConflicts(Lib.Database working, string table)
        {
            Lib.TableInfo info = working.Tables[table];
            string[] keys = info.PrimaryKeys.ToArray();
            string select = "SELECT * FROM `" + table + "`";
            string whereKeys = String.Join(" AND ", keys.Select(k => "`" + k + "` = ?"));

            using (Lib.View view = working.OpenView(select))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        var keyValues = new List<object>();
                        foreach (string key in keys)
                        {
                            int index = info.Columns.IndexOf(key) + 1;
                            keyValues.Add(row.IsNull(index) ? null : row[index]);
                        }

                        using (Lib.View existing = this.database.OpenView(select + " WHERE " + whereKeys))
                        {
                            existing.Execute(new Lib.Record(keyValues.ToArray()));
                            using (Lib.Record current = existing.Fetch())
                            {
                                if (current != null && !SameRow(row, current, info))
                                {
                                    string[] text = keyValues.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)).ToArray();
                                    this.AddError(MsmErrorType.msmErrorTableMerge, databaseTable: table, databaseKeys: text, moduleTable: table, moduleKeys: text);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static bool SameRow(Lib.Record left, Lib.Record right, Lib.TableInfo info)
        {
            for (int i = 1; i <= info.Columns.Count; i++)
            {
                if (info.Columns[i - 1].Type == typeof(Stream))
                {
                    continue;
                }

                if (left.IsNull(i) != right.IsNull(i) || (!left.IsNull(i) && !String.Equals(left.GetString(i), right.GetString(i), StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
        }

        // The lowest sequence number in each table that gets generated directory actions, before the module's actions are merged.
        private Dictionary<string, int> ReadLowestSequences()
        {
            var lowest = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string table in DirectoryActionSequenceTables)
            {
                if (this.database.Tables.Contains(table))
                {
                    Dictionary<string, int> sequences = this.ReadSequences(table);
                    if (sequences.Count > 0)
                    {
                        lowest[table] = sequences.Values.Min();
                    }
                }
            }

            return lowest;
        }

        // The standard folder a module directory is named after: its identifier (before the module suffix) is the folder's
        // name, optionally followed by '_' and a qualifier.
        private static string StandardFolderOf(string directory)
        {
            int dot = directory.IndexOf('.');
            if (dot <= 0 || dot == directory.Length - 1)
            {
                return null;
            }

            string id = directory.Substring(0, dot);
            if (StandardFolders.Contains(id))
            {
                return id;
            }

            int underscore = id.IndexOf('_');
            if (underscore > 0 && StandardFolders.Contains(id.Substring(0, underscore)))
            {
                return id.Substring(0, underscore);
            }

            return null;
        }

        private void AddDirectoryActions(IEnumerable<string> directories, Dictionary<string, int> lowestSequences)
        {
            // The generated actions go before the table's original first action, after anything the module already placed there.
            var generatedSequence = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string tableName in DirectoryActionSequenceTables)
            {
                int sequence = 1;
                if (lowestSequences.TryGetValue(tableName, out int lowest) && this.database.Tables.Contains(tableName))
                {
                    sequence = this.ReadSequences(tableName).Values.Where(v => v < lowest).DefaultIfEmpty(0).Max() + 1;
                }

                generatedSequence[tableName] = sequence;
            }

            foreach (string directory in directories)
            {
                string standard = StandardFolderOf(directory);
                if (standard == null)
                {
                    continue;
                }

                this.EnsureTable("CustomAction", "CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(255) PRIMARY KEY `Action`)");
                if (!this.RowExists("CustomAction", "Action", directory))
                {
                    this.database.InsertRowUnchecked("CustomAction", new Dictionary<string, object> { { "Action", directory }, { "Type", 51 }, { "Source", directory }, { "Target", "[" + standard + "]" } });
                }

                foreach (string tableName in DirectoryActionSequenceTables)
                {
                    this.WriteLog("Merging generated Directory actions into Database Table: " + tableName);
                    this.EnsureSequenceTable(tableName);
                    if (!this.RowExists(tableName, "Action", directory))
                    {
                        this.database.InsertRowUnchecked(tableName, new Dictionary<string, object> { { "Action", directory }, { "Sequence", generatedSequence[tableName] } });
                    }
                }
            }
        }

        private void MergeSequences(List<SequenceRow> rows)
        {
            foreach (IGrouping<string, SequenceRow> group in rows.GroupBy(r => r.Table))
            {
                string table = group.Key;
                this.WriteLog("Merging Sequence Table: Module" + table + " into Database Table: " + table);
                this.EnsureSequenceTable(table);

                Dictionary<string, int> sequences = this.ReadSequences(table);
                var placed = new HashSet<string>(StringComparer.Ordinal);
                List<SequenceRow> tableRows = group.ToList();
                var known = new HashSet<string>(tableRows.Select(r => r.Action), StringComparer.Ordinal);
                var anchors = new HashSet<string>(tableRows.Where(r => r.Sequence.HasValue).Select(r => r.Action), StringComparer.Ordinal);

                // Actions with a sequence number first, then the ones placed relative to another action.
                foreach (SequenceRow row in tableRows.Where(r => r.Sequence.HasValue))
                {
                    if (sequences.ContainsKey(row.Action) || this.RowExists(table, "Action", row.Action))
                    {
                        this.WriteLog("Base Action " + row.Action + " in " + table + " table already exists in MSI. Using MSI action.");
                        continue;
                    }

                    this.MakeRoom(table, sequences, row.Sequence.Value);
                    this.InsertAction(table, row.Action, row.Condition, row.Sequence.Value);
                    sequences[row.Action] = row.Sequence.Value;
                    placed.Add(row.Action);
                }

                // An action is placed relative to a base action the module lists with a sequence number, or one it has
                // already placed. mergemod.dll reports any other as orphaned in the module.
                var pending = tableRows.Where(r => !r.Sequence.HasValue && r.BaseAction != null).ToList();
                bool progress = true;
                while (pending.Count > 0 && progress)
                {
                    progress = false;
                    foreach (SequenceRow next in pending.ToList())
                    {
                        if (sequences.ContainsKey(next.Action) || this.RowExists(table, "Action", next.Action))
                        {
                            this.WriteLog("Base Action " + next.Action + " in " + table + " table already exists in MSI. Using MSI action.");
                            pending.Remove(next);
                            progress = true;
                        }
                        else if (!known.Contains(next.BaseAction))
                        {
                            this.Orphan(table, next);
                            pending.Remove(next);
                            progress = true;
                        }
                        else if ((anchors.Contains(next.BaseAction) || placed.Contains(next.BaseAction)) && sequences.ContainsKey(next.BaseAction))
                        {
                            this.WriteLog("Placing action " + next.Action + " " + (next.After ? "after " : "before ") + next.BaseAction);
                            this.Place(table, sequences, placed, next);
                            pending.Remove(next);
                            progress = true;
                        }
                    }
                }

                foreach (SequenceRow next in pending)
                {
                    this.Orphan(table, next);
                }
            }
        }

        private void Orphan(string table, SequenceRow row)
        {
            this.WriteLog("> Warning: Failed to merge action " + row.Action + " into " + table + " table. Action is orphaned in the module.");
            this.AddError(MsmErrorType.msmErrorResequenceMerge, moduleTable: "Module" + table, moduleKeys: new[] { row.Action });
        }

        private void Place(string table, Dictionary<string, int> sequences, HashSet<string> placed, SequenceRow row)
        {
            int baseSequence = sequences[row.BaseAction];
            int desired = row.After
                ? baseSequence + 1
                : sequences.Values.Where(v => v < baseSequence).DefaultIfEmpty(0).Max() + 1;

            // Actions this merge already placed there are stepped over (mergemod.dll puts a second action after the same
            // base action after the first one).
            while (sequences.Any(p => p.Value == desired && placed.Contains(p.Key)))
            {
                desired++;
            }

            this.MakeRoom(table, sequences, desired);
            this.InsertAction(table, row.Action, row.Condition, desired);
            sequences[row.Action] = desired;
            placed.Add(row.Action);
        }

        /// <summary>
        /// Frees a sequence number the way mergemod.dll does: the actions numbered from it up to the next multiple of a step
        /// (the largest power of ten, up to 1000, that divides the number) move up by the step, and any action they land on
        /// moves up by the step as well.
        /// </summary>
        private void MakeRoom(string table, Dictionary<string, int> sequences, int number)
        {
            if (!sequences.ContainsValue(number))
            {
                return;
            }

            int step = 1;
            while (number > 0 && step < 1000 && number % (step * 10) == 0)
            {
                step *= 10;
            }

            foreach (string action in sequences.Where(p => p.Value >= number && p.Value < number + step).OrderByDescending(p => p.Value).Select(p => p.Key).ToList())
            {
                this.MoveAction(table, sequences, action, sequences[action] + step, step);
            }
        }

        private void MoveAction(string table, Dictionary<string, int> sequences, string action, int to, int step)
        {
            string occupant = sequences.Where(p => p.Value == to && p.Key != action).Select(p => p.Key).FirstOrDefault();
            if (occupant != null)
            {
                this.MoveAction(table, sequences, occupant, to + step, step);
            }

            this.database.Execute("UPDATE `" + table + "` SET `Sequence` = ? WHERE `Action` = ?", new Lib.Record(to, action));
            sequences[action] = to;
        }

        private void InsertAction(string table, string action, string condition, int sequence)
        {
            var values = new Dictionary<string, object> { { "Action", action }, { "Sequence", sequence } };
            if (condition != null)
            {
                values["Condition"] = condition;
            }

            this.database.InsertRowUnchecked(table, values);
        }

        private Dictionary<string, int> ReadSequences(string table)
        {
            var sequences = new Dictionary<string, int>(StringComparer.Ordinal);
            using (Lib.View view = this.database.OpenView("SELECT `Action`, `Sequence` FROM `" + table + "`"))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        if (!row.IsNull(2))
                        {
                            sequences[row.GetString(1)] = row.GetInteger(2);
                        }
                    }
                }
            }

            return sequences;
        }

        private void ConnectComponents(string feature)
        {
            this.WriteLog("Connecting Merge Module Components to Feature: " + feature);

            this.EnsureTable("FeatureComponents", "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)");

            if (!this.module.Tables.Contains("Component"))
            {
                return;
            }

            foreach (string component in this.ReadStrings(this.module, "SELECT `Component` FROM `Component`"))
            {
                this.WriteLog("   o Connecting Component: " + component);
                if (String.IsNullOrEmpty(feature))
                {
                    this.WriteLog(">> Error: Failed to connect component " + component + ". Feature required.");
                    this.AddError(MsmErrorType.msmErrorFeatureRequired, moduleTable: "Component", moduleKeys: new[] { component });
                    continue;
                }

                using (Lib.View view = this.database.OpenView("SELECT `Feature_` FROM `FeatureComponents` WHERE `Feature_` = ? AND `Component_` = ?"))
                {
                    view.Execute(new Lib.Record(feature, component));
                    using (Lib.Record existing = view.Fetch())
                    {
                        if (existing != null)
                        {
                            continue;
                        }
                    }
                }

                this.database.InsertRowUnchecked("FeatureComponents", new Dictionary<string, object> { { "Feature_", feature }, { "Component_", component } });
            }
        }

        private void EnsureSequenceTable(string table)
        {
            this.EnsureTable(table, "CREATE TABLE `" + table + "` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)");
        }

        private void EnsureTable(string table, string createStatement)
        {
            if (!this.database.Tables.Contains(table))
            {
                this.database.Execute(createStatement);
            }
        }

        private bool RowExists(string table, string keyColumn, string key)
        {
            using (Lib.View view = this.database.OpenView("SELECT `" + keyColumn + "` FROM `" + table + "` WHERE `" + keyColumn + "` = ?"))
            {
                view.Execute(new Lib.Record(key));
                using (Lib.Record row = view.Fetch())
                {
                    return row != null;
                }
            }
        }

        private List<Signature> ReadSignatures(Lib.Database source)
        {
            var signatures = new List<Signature>();
            using (Lib.View view = source.OpenView("SELECT `ModuleID`, `Language`, `Version` FROM `ModuleSignature`"))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        signatures.Add(new Signature { Id = row.GetString(1), Language = row.GetInteger(2), Version = row.GetString(3) });
                    }
                }
            }

            return signatures;
        }

        private List<string> ReadStrings(Lib.Database source, string query)
        {
            var values = new List<string>();
            using (Lib.View view = source.OpenView(query))
            {
                view.Execute();
                for (Lib.Record row = view.Fetch(); row != null; row = view.Fetch())
                {
                    using (row)
                    {
                        values.Add(row.GetString(1));
                    }
                }
            }

            return values;
        }

        private static string ReadTemplate(string path)
        {
            using (var summary = new Lib.SummaryInfo(path, false))
            {
                return summary.Template;
            }
        }

        private static string FirstFrames(Exception e)
        {
            string[] lines = (e.StackTrace ?? String.Empty).Split('\n');
            return String.Join(" <- ", lines.Take(9).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @" in .*[\\/]([^\\/]+):line (\d+)", " @$1:$2")));
        }

        private static List<int> ParseLanguages(string template)
        {
            var languages = new List<int>();
            int semicolon = template?.IndexOf(';') ?? -1;
            if (semicolon >= 0)
            {
                foreach (string part in template.Substring(semicolon + 1).Split(','))
                {
                    if (Int32.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int language))
                    {
                        languages.Add(language);
                    }
                }
            }

            return languages;
        }

        private static HashSet<string> ParsePlatforms(string template)
        {
            int semicolon = template?.IndexOf(';') ?? -1;
            string platforms = semicolon >= 0 ? template.Substring(0, semicolon) : template ?? String.Empty;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string platform in platforms.Split(','))
            {
                set.Add(platform.Trim().Length == 0 ? "Intel" : platform.Trim());
            }

            return set;
        }

        private static bool PlatformsOverlap(string moduleTemplate, string databaseTemplate)
        {
            // A module for the default platform (Intel, or none) merges into any database, as with mergemod.dll.
            HashSet<string> modulePlatforms = ParsePlatforms(moduleTemplate);
            return modulePlatforms.Contains("Intel") || ParsePlatforms(databaseTemplate).Any(modulePlatforms.Contains);
        }

        private static bool LanguageExcluded(int excluded, int language)
        {
            if (excluded == 0)
            {
                return true;
            }

            return excluded > 0 ? excluded == language : -excluded != language;
        }

        private static int CompareVersions(string left, string right)
        {
            string[] a = left.Split('.');
            string[] b = right.Split('.');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                long x = i < a.Length && Int64.TryParse(a[i], out long px) ? px : 0;
                long y = i < b.Length && Int64.TryParse(b[i], out long py) ? py : 0;
                if (x != y)
                {
                    return x < y ? -1 : 1;
                }
            }

            return 0;
        }

        private void AddError(MsmErrorType type, string path = null, int language = -1, string databaseTable = null, string[] databaseKeys = null, string moduleTable = null, string[] moduleKeys = null)
        {
            this.errors.Add(new MsmErrorItem
            {
                Type = type,
                Path = path,
                Language = (short)language,
                DatabaseTable = databaseTable,
                DatabaseKeys = new MsmStringList(databaseKeys ?? new string[0]),
                ModuleTable = moduleTable,
                ModuleKeys = new MsmStringList(moduleKeys ?? new string[0]),
            });
        }

        private void WriteLog(string message)
        {
            this.log?.WriteLine(message);
            this.log?.Flush();
        }

        private void RequireDatabase()
        {
            if (this.database == null)
            {
                throw Fail("No database is open.");
            }
        }

        private void RequireModule()
        {
            if (this.module == null)
            {
                throw Fail("No merge module is open.");
            }
        }

        private static COMException Fail(string message)
        {
            return new COMException(message, EFail);
        }

        private sealed class Signature
        {
            public string Id;
            public int Language;
            public string Version;
        }

        private sealed class ConfigurationItem
        {
            public int Format;
            public string DefaultValue;
        }

        private sealed class SequenceRow
        {
            public string Table;
            public string Action;
            public int? Sequence;
            public string BaseAction;
            public bool After;
            public string Condition;
        }

        private sealed class MsmStringList : IMsmStrings
        {
            private readonly IList<string> items;

            public MsmStringList(IList<string> items)
            {
                this.items = items;
            }

            public string this[int index] => this.items[index - 1];

            public int Count => this.items.Count;
        }

        private sealed class MsmErrorItem : IMsmError
        {
            public MsmErrorType Type { get; set; }

            public string Path { get; set; }

            public short Language { get; set; }

            public string DatabaseTable { get; set; }

            public IMsmStrings DatabaseKeys { get; set; }

            public string ModuleTable { get; set; }

            public IMsmStrings ModuleKeys { get; set; }
        }

        private sealed class MsmErrorList : IMsmErrors
        {
            private readonly List<MsmErrorItem> items;

            public MsmErrorList(List<MsmErrorItem> items)
            {
                this.items = items.ToList();
            }

            public IMsmError this[int index] => this.items[index - 1];

            public int Count => this.items.Count;
        }
    }
}
