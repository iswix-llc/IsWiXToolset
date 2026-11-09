// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixInternal.MSTestSupport
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using WixToolset.Dtf.Compression.Cab;
    using WixToolset.Dtf.WindowsInstaller;
    using Managed = IsWiX.WindowsInstaller;
    using ManagedCab = IsWiX.Compression.Cab;

    /// <summary>
    /// Reads packages and cabinets back for tests. Windows uses msi.dll and cabinet.dll through Dtf. Everywhere else,
    /// and on Windows when WIX_MANAGED_MSI=1, the managed IsWiX.WindowsInstaller and IsWiX.Compression.Cab readers are
    /// used, so the tests run without msi.dll.
    /// </summary>
    public class Query
    {
        private const string ManagedEnvironmentVariableName = "WIX_MANAGED_MSI";

        private static readonly bool useManaged = DetectManaged();

        /// <summary>
        /// True when packages are read with the managed engine: WIX_MANAGED_MSI if set, otherwise everywhere but Windows.
        /// </summary>
        public static bool UseManagedReader => useManaged;

        private static bool DetectManaged()
        {
            var value = Environment.GetEnvironmentVariable(ManagedEnvironmentVariableName)?.Trim();

            if (!String.IsNullOrEmpty(value))
            {
                return value == "1" || String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }

            return !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public static string[] QueryDatabase(string path, string[] tables)
        {
            var results = new List<string>();
            var resultsByTable = QueryDatabaseByTable(path, tables);
            var sortedTables = tables.ToList();
            sortedTables.Sort();
            foreach (var tableName in sortedTables)
            {
                var rows = resultsByTable[tableName];
                rows?.ForEach(r => results.Add($"{tableName}:{r}"));
            }
            return results.ToArray();
        }

        /// <summary>
        /// Applies a transform to a copy of the database in memory (never saved) and queries the result.
        /// </summary>
        public static string[] QueryDatabaseWithTransform(string path, string transformPath, string[] tables)
        {
            var sortedTables = tables.ToList();
            sortedTables.Sort();
            Dictionary<string, List<string>> resultsByTable;

            if (useManaged)
            {
                using (var db = new Managed.Database(path, Managed.DatabaseOpenMode.Transact))
                {
                    db.ApplyTransform(transformPath);
                    resultsByTable = QueryDatabaseByTable(db, tables);
                }
            }
            else
            {
                using (var db = new Database(path, DatabaseOpenMode.Transact))
                {
                    db.ApplyTransform(transformPath);
                    resultsByTable = QueryDatabaseByTable(db, tables);
                }
            }

            var results = new List<string>();
            foreach (var tableName in sortedTables)
            {
                resultsByTable[tableName]?.ForEach(r => results.Add($"{tableName}:{r}"));
            }
            return results.ToArray();
        }

        public static string[] QueryDatabase(Database db, string[] tables)
        {
            var results = new List<string>();
            var resultsByTable = QueryDatabaseByTable(db, tables);
            var sortedTables = tables.ToList();
            sortedTables.Sort();
            foreach (var tableName in sortedTables)
            {
                var rows = resultsByTable[tableName];
                rows?.ForEach(r => results.Add($"{tableName}:{r}"));
            }
            return results.ToArray();
        }

        /// <summary>
        /// Returns rows from requested tables formatted to facilitate testing.
        /// If the table did not exist in the database, its list will be null.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="tables"></param>
        /// <returns></returns>
        public static Dictionary<string, List<string>> QueryDatabaseByTable(string path, string[] tables)
        {
            var results = new Dictionary<string, List<string>>();

            if (tables?.Length > 0)
            {
                if (useManaged)
                {
                    using (var db = new Managed.Database(path))
                    {
                        results = QueryDatabaseByTable(db, tables);
                    }
                }
                else
                {
                    using (var db = new Database(path))
                    {
                        results = QueryDatabaseByTable(db, tables);
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Returns rows from requested tables formatted to facilitate testing.
        /// If the table did not exist in the database, its list will be null.
        /// </summary>
        /// <param name="db"></param>
        /// <param name="tables"></param>
        /// <returns></returns>
        public static Dictionary<string, List<string>> QueryDatabaseByTable(Database db, string[] tables)
        {
            var results = new Dictionary<string, List<string>>();

            if (tables?.Length > 0)
            {
                var sb = new StringBuilder();

                foreach (var table in tables)
                {
                    if (table == "_SummaryInformation")
                    {
                        var entries = new List<string>();
                        results.Add(table, entries);

                        entries.Add($"Title\t{db.SummaryInfo.Title}");
                        entries.Add($"Subject\t{db.SummaryInfo.Subject}");
                        entries.Add($"Author\t{db.SummaryInfo.Author}");
                        entries.Add($"Keywords\t{db.SummaryInfo.Keywords}");
                        entries.Add($"Comments\t{db.SummaryInfo.Comments}");
                        entries.Add($"Template\t{db.SummaryInfo.Template}");
                        entries.Add($"CodePage\t{db.SummaryInfo.CodePage}");
                        entries.Add($"PageCount\t{db.SummaryInfo.PageCount}");
                        entries.Add($"WordCount\t{db.SummaryInfo.WordCount}");
                        entries.Add($"CharacterCount\t{db.SummaryInfo.CharacterCount}");
                        entries.Add($"Security\t{db.SummaryInfo.Security}");

                        continue;
                    }

                    if (!db.IsTablePersistent(table))
                    {
                        results.Add(table, null);
                        continue;
                    }

                    var rows = new List<string>();
                    results.Add(table, rows);

                    using (var view = db.OpenView("SELECT * FROM `{0}`", table))
                    {
                        view.Execute();

                        Record record;
                        while ((record = view.Fetch()) != null)
                        {
                            sb.Clear();

                            using (record)
                            {
                                for (var i = 0; i < record.FieldCount; ++i)
                                {
                                    if (i > 0)
                                    {
                                        sb.Append("\t");
                                    }

                                    sb.Append(record[i + 1]?.ToString());
                                }
                            }

                            rows.Add(sb.ToString());
                        }
                    }

                    rows.Sort();
               }
            }

            return results;
        }

        /// <summary>
        /// The managed counterpart of <see cref="QueryDatabaseByTable(Database, string[])"/>; the rows are formatted the same way.
        /// </summary>
        public static Dictionary<string, List<string>> QueryDatabaseByTable(Managed.Database db, string[] tables)
        {
            var results = new Dictionary<string, List<string>>();

            if (tables?.Length > 0)
            {
                var sb = new StringBuilder();

                foreach (var table in tables)
                {
                    if (table == "_SummaryInformation")
                    {
                        var entries = new List<string>();
                        results.Add(table, entries);

                        entries.Add($"Title\t{db.SummaryInfo.Title}");
                        entries.Add($"Subject\t{db.SummaryInfo.Subject}");
                        entries.Add($"Author\t{db.SummaryInfo.Author}");
                        entries.Add($"Keywords\t{db.SummaryInfo.Keywords}");
                        entries.Add($"Comments\t{db.SummaryInfo.Comments}");
                        entries.Add($"Template\t{db.SummaryInfo.Template}");
                        entries.Add($"CodePage\t{db.SummaryInfo.CodePage}");
                        entries.Add($"PageCount\t{db.SummaryInfo.PageCount}");
                        entries.Add($"WordCount\t{db.SummaryInfo.WordCount}");
                        entries.Add($"CharacterCount\t{db.SummaryInfo.CharacterCount}");
                        entries.Add($"Security\t{db.SummaryInfo.Security}");

                        continue;
                    }

                    if (!db.IsTablePersistent(table))
                    {
                        results.Add(table, null);
                        continue;
                    }

                    var rows = new List<string>();
                    results.Add(table, rows);

                    using (var view = db.OpenView("SELECT * FROM `{0}`", table))
                    {
                        view.Execute();

                        Managed.Record record;
                        while ((record = view.Fetch()) != null)
                        {
                            sb.Clear();

                            using (record)
                            {
                                for (var i = 0; i < record.FieldCount; ++i)
                                {
                                    if (i > 0)
                                    {
                                        sb.Append("\t");
                                    }

                                    sb.Append(record[i + 1]?.ToString());
                                }
                            }

                            rows.Add(sb.ToString());
                        }
                    }

                    rows.Sort();
                }
            }

            return results;
        }

        /// <summary>
        /// Resolves each File row to its install target path (e.g. PFiles\Dir\file.txt) the way
        /// the Dtf InstallPackage does, using only the managed database reader.
        /// </summary>
        public static SortedDictionary<string, string> GetFileTargetPaths(string path)
        {
            using (var db = new Managed.Database(path))
            {
                var dirs = new Dictionary<string, string[]>(StringComparer.Ordinal);
                using (var view = db.OpenView("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`"))
                {
                    view.Execute();
                    Managed.Record r;
                    while ((r = view.Fetch()) != null)
                    {
                        using (r) { dirs[r.GetString(1)] = new[] { r.GetString(2), r.GetString(3) }; }
                    }
                }

                var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
                string Resolve(string id)
                {
                    if (resolved.TryGetValue(id, out var done)) { return done; }
                    var d = dirs[id];
                    var name = d[1] ?? string.Empty;
                    var colon = name.IndexOf(':');
                    if (colon >= 0) { name = name.Substring(0, colon); }
                    var bar = name.IndexOf('|');
                    if (bar >= 0) { name = name.Substring(bar + 1); }
                    string result;
                    if (string.IsNullOrEmpty(d[0]) || d[0] == id)
                    {
                        result = string.Empty;
                    }
                    else
                    {
                        var parent = Resolve(d[0]);
                        result = name == "." ? parent : (parent.Length == 0 ? name : parent + "\\" + name);
                    }
                    resolved[id] = result;
                    return result;
                }

                var compDirs = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var view = db.OpenView("SELECT `Component`, `Directory_` FROM `Component`"))
                {
                    view.Execute();
                    Managed.Record r;
                    while ((r = view.Fetch()) != null)
                    {
                        using (r) { compDirs[r.GetString(1)] = r.GetString(2); }
                    }
                }

                var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
                using (var view = db.OpenView("SELECT `File`, `Component_`, `FileName` FROM `File`"))
                {
                    view.Execute();
                    Managed.Record r;
                    while ((r = view.Fetch()) != null)
                    {
                        using (r)
                        {
                            var name = r.GetString(3) ?? string.Empty;
                            var bar = name.IndexOf('|');
                            if (bar >= 0) { name = name.Substring(bar + 1); }
                            var dir = Resolve(compDirs[r.GetString(2)]);
                            files[r.GetString(1)] = dir.Length == 0 ? name : dir + "\\" + name;
                        }
                    }
                }

                return files;
            }
        }

        public static CabinetFile[] GetCabinetFiles(string path)
        {
            if (useManaged)
            {
                var managedCab = new ManagedCab.CabInfo(path);

                return managedCab.GetFiles().Select(f => new CabinetFile(f.Name, f.Length, f.OpenRead)).ToArray();
            }

            var cab = new CabInfo(path);

            return cab.GetFiles().Select(f => new CabinetFile(f.Name, f.Length, f.OpenRead)).ToArray();
        }

        public static void ExtractStream(string path, string streamName, string outputPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            if (useManaged)
            {
                using (var db = new Managed.Database(path))
                using (var view = db.OpenView("SELECT `Data` FROM `_Streams` WHERE `Name` = '{0}'", streamName))
                {
                    view.Execute();

                    using (var record = view.Fetch())
                    {
                        record.GetStream(1, outputPath);
                    }
                }

                return;
            }

            using (var db = new Database(path))
            using (var view = db.OpenView("SELECT `Data` FROM `_Streams` WHERE `Name` = '{0}'", streamName))
            {
                view.Execute();

                using (var record = view.Fetch())
                {
                    record.GetStream(1, outputPath);
                }
            }
        }

        public static void ExtractSubStorage(string path, string subStorageName, string outputPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            if (useManaged)
            {
                using (var db = new Managed.Database(path))
                using (var view = db.OpenView("SELECT `Name`, `Data` FROM `_Storages` WHERE `Name` = '{0}'", subStorageName))
                {
                    view.Execute();

                    using (var record = view.Fetch())
                    {
                        var name = record.GetString(1);
                        record.GetStream(2, outputPath);
                    }
                }

                return;
            }

            using (var db = new Database(path))
            using (var view = db.OpenView("SELECT `Name`, `Data` FROM `_Storages` WHERE `Name` = '{0}'", subStorageName))
            {
                view.Execute();

                using (var record = view.Fetch())
                {
                    var name = record.GetString(1);
                    record.GetStream(2, outputPath);
                }
            }
        }

        public static string[] GetSubStorageNames(string path)
        {
            var result = new List<string>();

            if (useManaged)
            {
                using (var db = new Managed.Database(path))
                using (var view = db.OpenView("SELECT `Name` FROM `_Storages`"))
                {
                    view.Execute();

                    Managed.Record record;
                    while ((record = view.Fetch()) != null)
                    {
                        var name = record.GetString(1);
                        result.Add(name);
                    }
                }
            }
            else
            {
                using (var db = new Database(path))
                using (var view = db.OpenView("SELECT `Name` FROM `_Storages`"))
                {
                    view.Execute();

                    Record record;
                    while ((record = view.Fetch()) != null)
                    {
                        var name = record.GetString(1);
                        result.Add(name);
                    }
                }
            }

            result.Sort();
            return result.ToArray();
        }
    }

    /// <summary>
    /// A file inside a cabinet, read by either the Windows or the managed cabinet reader.
    /// </summary>
    public sealed class CabinetFile
    {
        private readonly Func<Stream> openRead;

        public CabinetFile(string name, long length, Func<Stream> openRead)
        {
            this.Name = name;
            this.Length = length;
            this.openRead = openRead;
        }

        public string Name { get; }

        public long Length { get; }

        public Stream OpenRead()
        {
            return this.openRead();
        }

        public StreamReader OpenText()
        {
            return new StreamReader(this.openRead());
        }
    }
}
