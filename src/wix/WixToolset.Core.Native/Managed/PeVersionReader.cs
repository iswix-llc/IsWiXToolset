// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Managed
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Minimal PE reader that extracts the VS_VERSION_INFO resource (file version and language list),
    /// as MsiGetFileVersion does, and the version of a TrueType font. Works on any platform.
    /// </summary>
    internal static class PeVersionReader
    {
        private const int RtVersion = 16;

        /// <summary>Returns false if the file is not a PE or has no version resource.</summary>
        internal static bool TryRead(string path, out string version, out string language)
        {
            version = String.Empty;
            language = String.Empty;

            var data = File.ReadAllBytes(path);

            try
            {
                if (TryParse(data, out version, out language))
                {
                    language = AddMuiLanguages(path, language);
                    return true;
                }

                return TryParseFont(data, out version);
            }
            catch (ArgumentOutOfRangeException)
            {
                version = String.Empty;
                language = String.Empty;
                return false;
            }
            catch (IndexOutOfRangeException)
            {
                version = String.Empty;
                language = String.Empty;
                return false;
            }
        }

        private static bool TryParse(byte[] d, out string version, out string language)
        {
            version = String.Empty;
            language = String.Empty;

            if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z')
            {
                return false;
            }

            var pe = (int)U32(d, 0x3C);
            if (pe < 0 || pe + 24 > d.Length || U32(d, pe) != 0x00004550)
            {
                return false;
            }

            var sectionCount = U16(d, pe + 6);
            var optionalSize = U16(d, pe + 20);
            var optional = pe + 24;
            var magic = U16(d, optional);
            var dataDirectory = optional + (magic == 0x20B ? 112 : 96);
            var resourceRva = U32(d, dataDirectory + 16);
            if (resourceRva == 0)
            {
                return false;
            }

            var sections = optional + optionalSize;

            int RvaToOffset(uint rva)
            {
                for (var i = 0; i < sectionCount; i++)
                {
                    var s = sections + (i * 40);
                    var virtualSize = U32(d, s + 8);
                    var virtualAddress = U32(d, s + 12);
                    var rawSize = U32(d, s + 16);
                    var rawPointer = U32(d, s + 20);
                    var extent = Math.Max(virtualSize, rawSize);
                    if (rva >= virtualAddress && rva < virtualAddress + extent)
                    {
                        return (int)(rawPointer + (rva - virtualAddress));
                    }
                }

                return -1;
            }

            var root = RvaToOffset(resourceRva);
            if (root < 0)
            {
                return false;
            }

            // Level 1: resource type.
            var typeDir = FindEntry(d, root, root, RtVersion);
            if (typeDir < 0)
            {
                return false;
            }

            // Level 2: first name/id.
            var nameDir = FirstEntry(d, root, typeDir);
            if (nameDir < 0)
            {
                return false;
            }

            // Level 3: first language -> data entry.
            var langEntryOffset = FirstEntryRaw(d, root, nameDir, out var languageId);
            if (langEntryOffset < 0)
            {
                return false;
            }

            var dataRva = U32(d, langEntryOffset);
            var dataSize = (int)U32(d, langEntryOffset + 4);
            var blob = RvaToOffset(dataRva);
            if (blob < 0 || dataSize < 92 || blob + dataSize > d.Length)
            {
                return false;
            }

            // VS_VERSIONINFO: wLength, wValueLength, wType, szKey (UTF-16), padding, VS_FIXEDFILEINFO.
            const string key = "VS_VERSION_INFO";
            var keyEnd = blob + 6 + ((key.Length + 1) * 2);
            // Alignment is relative to the start of the resource blob.
            var fixedInfo = blob + (((keyEnd - blob) + 3) & ~3);

            if (U32(d, fixedInfo) != 0xFEEF04BD)
            {
                return false;
            }

            var ms = U32(d, fixedInfo + 8);
            var ls = U32(d, fixedInfo + 12);
            version = String.Format("{0}.{1}.{2}.{3}", ms >> 16, ms & 0xFFFF, ls >> 16, ls & 0xFFFF);

            var languages = FindTranslations(d, blob, dataSize);
            language = languages.Length > 0 ? languages : languageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        // Windows Installer also reports the languages of satellite resource files: each language subfolder next to the
        // file that holds "<file name>.mui" adds its language. Subfolders are visited in ascending name order and each language
        // not already listed is put at the front, so the file's own language ends up last (for example "1036,1031,1033").
        internal static string AddMuiLanguages(string path, string language)
        {
            var languages = new System.Collections.Generic.List<int>();
            foreach (var part in language.Split(','))
            {
                if (Int32.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
                {
                    languages.Add(id);
                }
            }

            var found = false;
            var muiName = Path.GetFileName(path) + ".mui";
            foreach (var folderName in MuiFolders(Path.GetDirectoryName(Path.GetFullPath(path)), muiName))
            {
                if (TryGetLanguageId(folderName, out var id))
                {
                    languages.Add(id);
                    found = true;
                }
            }

            if (!found)
            {
                return language;
            }

            var result = new System.Collections.Generic.List<int>();
            foreach (var id in languages)
            {
                if (!result.Contains(id))
                {
                    result.Insert(0, id);
                }
            }

            return String.Join(",", result.ConvertAll(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        }

        // The .mui files of each subfolder of a directory, listed once per directory because a payload folder can hold hundreds of files
        // and dozens of language folders. File names are compared ignoring case, since they are case-sensitive on Linux.
        private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.HashSet<string>>>> MuiCache =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.HashSet<string>>>>(StringComparer.Ordinal);

        // The names of the subfolders of a directory that hold the given .mui file, in ascending order (ignoring case, as NTFS lists them).
        private static System.Collections.Generic.List<string> MuiFolders(string directory, string muiName)
        {
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.HashSet<string>>> listing;
            lock (MuiCache)
            {
                if (!MuiCache.TryGetValue(directory, out listing))
                {
                    listing = ListMuiFiles(directory);
                    MuiCache[directory] = listing;
                }
            }

            var folders = new System.Collections.Generic.List<string>();
            foreach (var entry in listing)
            {
                if (entry.Value.Contains(muiName))
                {
                    folders.Add(entry.Key);
                }
            }

            return folders;
        }

        private static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.HashSet<string>>> ListMuiFiles(string directory)
        {
            var listing = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.HashSet<string>>>();
            try
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var subfolder in Directory.GetDirectories(directory))
                {
                    names.Add(Path.GetFileName(subfolder));
                }

                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var files = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in Directory.GetFiles(Path.Combine(directory, name), "*.mui"))
                    {
                        files.Add(Path.GetFileName(file));
                    }

                    if (files.Count > 0)
                    {
                        listing.Add(new System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.HashSet<string>>(name, files));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return listing;
        }

        private static bool TryGetLanguageId(string cultureName, out int id)
        {
            if (MuiLanguageIds.TryGetValue(cultureName, out id))
            {
                return true;
            }

            try
            {
                id = System.Globalization.CultureInfo.GetCultureInfo(cultureName).LCID;
                return id != 0 && id != 0x1000 && id != 0x7f;
            }
            catch (System.Globalization.CultureNotFoundException)
            {
                id = 0;
                return false;
            }
        }

        // The language ids Windows reports for the culture folders it ships, so the answer does not depend on the culture data of the machine.
        private static readonly System.Collections.Generic.Dictionary<string, int> MuiLanguageIds = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "af-ZA", 1078 }, { "am-ET", 1118 }, { "ar-SA", 1025 }, { "as-IN", 1101 }, { "az-Latn-AZ", 1068 }, { "bg-BG", 1026 },
            { "bn-IN", 1093 }, { "bs-Latn-BA", 5146 }, { "ca-ES", 1027 }, { "ca-Es-VALENCIA", 2051 }, { "cs-CZ", 1029 }, { "cy-GB", 1106 },
            { "da-DK", 1030 }, { "de-DE", 1031 }, { "el-GR", 1032 }, { "en-GB", 2057 }, { "en-US", 1033 }, { "es-ES", 3082 }, { "es-MX", 2058 },
            { "et-EE", 1061 }, { "eu-ES", 1069 }, { "fa-IR", 1065 }, { "fi-FI", 1035 }, { "fil-PH", 1124 }, { "fr-CA", 3084 }, { "fr-FR", 1036 },
            { "ga-IE", 2108 }, { "gd-GB", 1169 }, { "gl-ES", 1110 }, { "gu-IN", 1095 }, { "he-IL", 1037 }, { "hi-IN", 1081 }, { "hr-HR", 1050 },
            { "hu-HU", 1038 }, { "hy-AM", 1067 }, { "id-ID", 1057 }, { "is-IS", 1039 }, { "it-IT", 1040 }, { "ja-JP", 1041 }, { "ka-GE", 1079 },
            { "kk-KZ", 1087 }, { "km-KH", 1107 }, { "kn-IN", 1099 }, { "ko-KR", 1042 }, { "kok-IN", 1111 }, { "lb-LU", 1134 }, { "lo-LA", 1108 },
            { "lt-LT", 1063 }, { "lv-LV", 1062 }, { "mi-NZ", 1153 }, { "mk-MK", 1071 }, { "ml-IN", 1100 }, { "mr-IN", 1102 }, { "ms-MY", 1086 },
            { "mt-MT", 1082 }, { "nb-NO", 1044 }, { "ne-NP", 1121 }, { "nl-NL", 1043 }, { "nn-NO", 2068 }, { "or-IN", 1096 }, { "pa-IN", 1094 },
            { "pl-PL", 1045 }, { "pt-BR", 1046 }, { "pt-PT", 2070 }, { "quz-PE", 3179 }, { "ro-RO", 1048 }, { "ru-RU", 1049 }, { "sk-SK", 1051 },
            { "sl-SI", 1060 }, { "sq-AL", 1052 }, { "sr-Cyrl-BA", 7194 }, { "sr-Cyrl-RS", 10266 }, { "sr-Latn-RS", 9242 }, { "sv-SE", 1053 },
            { "ta-IN", 1097 }, { "te-IN", 1098 }, { "th-TH", 1054 }, { "tr-TR", 1055 }, { "tt-RU", 1092 }, { "ug-CN", 1152 }, { "uk-UA", 1058 },
            { "ur-PK", 1056 }, { "uz-Latn-UZ", 1091 }, { "vi-VN", 1066 }, { "zh-CN", 2052 }, { "zh-TW", 1028 },
        };

        // The language list MsiGetFileVersion reports: every language id in the Translation array, in file order and
        // without repeats, joined with commas (a file with one translation gives just that id).
        private static string FindTranslations(byte[] d, int blob, int size)
        {
            var needle = Encoding.Unicode.GetBytes("Translation");

            for (var i = blob; i + needle.Length + 6 < blob + size; i += 2)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (d[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    // The Var structure starts with wLength, wValueLength (bytes) and wType, 6 bytes before the key.
                    var valueLength = i >= blob + 6 ? U16(d, i - 4) : 4;
                    var valueStart = i + needle.Length + 2; // skip terminating NUL
                    valueStart = blob + (((valueStart - blob) + 3) & ~3);
                    var count = Math.Max(1, valueLength / 4);
                    var ids = new System.Collections.Generic.List<int>();
                    for (var k = 0; k < count && valueStart + (k * 4) + 2 <= blob + size; k++)
                    {
                        var id = U16(d, valueStart + (k * 4));
                        if (!ids.Contains(id))
                        {
                            ids.Add(id);
                        }
                    }

                    return String.Join(",", ids.ConvertAll(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
                }
            }

            return String.Empty;
        }

        // The version of a TrueType or OpenType font, read from its name table (name id 5, for example "Version 1.10"),
        // as "1.10.0.0". MsiGetFileVersion reports a version for fonts and no language.
        private static bool TryParseFont(byte[] d, out string version)
        {
            version = String.Empty;
            if (d.Length < 12)
            {
                return false;
            }

            var tag = BE32(d, 0);
            if (tag != 0x00010000 && tag != 0x74727565 && tag != 0x4F54544F)
            {
                return false;
            }

            var tableCount = BE16(d, 4);
            for (var t = 0; t < tableCount; t++)
            {
                var record = 12 + (t * 16);
                if (record + 16 > d.Length || BE32(d, record) != 0x6E616D65)
                {
                    continue;
                }

                var table = (int)BE32(d, record + 8);
                var count = BE16(d, table + 2);
                var strings = table + BE16(d, table + 4);
                string best = null;
                for (var n = 0; n < count; n++)
                {
                    var entry = table + 6 + (n * 12);
                    if (BE16(d, entry + 6) != 5)
                    {
                        continue;
                    }

                    var platform = BE16(d, entry);
                    var length = BE16(d, entry + 8);
                    var offset = strings + BE16(d, entry + 10);
                    var text = platform == 3 || platform == 0 ? Encoding.BigEndianUnicode.GetString(d, offset, length) : Encoding.ASCII.GetString(d, offset, length);
                    if (best == null || (platform == 3 && BE16(d, entry + 4) == 0x409))
                    {
                        best = text;
                    }
                }

                return best != null && TryFontVersion(best, out version);
            }

            return false;
        }

        // "Version 1.10; ttfautohint" gives 1.10.0.0: the first number, a dot, and the digits after it.
        private static bool TryFontVersion(string text, out string version)
        {
            version = String.Empty;
            var i = 0;
            while (i < text.Length && !Char.IsDigit(text[i]))
            {
                i++;
            }

            var majorStart = i;
            while (i < text.Length && Char.IsDigit(text[i]))
            {
                i++;
            }

            if (i == majorStart || i >= text.Length || text[i] != '.')
            {
                return false;
            }

            var minorStart = ++i;
            while (i < text.Length && Char.IsDigit(text[i]))
            {
                i++;
            }

            if (i == minorStart)
            {
                return false;
            }

            if (!UInt16.TryParse(text.Substring(majorStart, minorStart - 1 - majorStart), out var major) || !UInt16.TryParse(text.Substring(minorStart, i - minorStart), out var minor))
            {
                return false;
            }

            version = String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}.{1}.0.0", major, minor);
            return true;
        }

        // Finds the entry with the given integer id in the directory at dirOffset; returns the offset of its subdirectory.
        private static int FindEntry(byte[] d, int root, int dirOffset, int id)
        {
            var named = U16(d, dirOffset + 12);
            var ids = U16(d, dirOffset + 14);
            var entries = dirOffset + 16 + (named * 8);

            for (var i = 0; i < ids; i++)
            {
                var e = entries + (i * 8);
                if (U32(d, e) == (uint)id)
                {
                    var target = U32(d, e + 4);
                    return (target & 0x80000000) != 0 ? root + (int)(target & 0x7FFFFFFF) : -1;
                }
            }

            return -1;
        }

        private static int FirstEntry(byte[] d, int root, int dirOffset)
        {
            var entry = FirstEntryRaw(d, root, dirOffset, out _);
            if (entry < 0)
            {
                return -1;
            }

            // FirstEntryRaw returns the data-entry offset for leaf targets; for subdirectories the caller needs the directory.
            return entry;
        }

        // Returns the offset the first entry points at (subdirectory or data entry) and its id.
        private static int FirstEntryRaw(byte[] d, int root, int dirOffset, out int id)
        {
            id = 0;
            var count = U16(d, dirOffset + 12) + U16(d, dirOffset + 14);
            if (count == 0)
            {
                return -1;
            }

            var e = dirOffset + 16;
            id = (int)(U32(d, e) & 0xFFFF);
            return root + (int)(U32(d, e + 4) & 0x7FFFFFFF);
        }

        private static int BE16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

        private static uint BE32(byte[] d, int o) => unchecked((uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]));

        private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);

        private static uint U32(byte[] d, int o) => unchecked((uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)));
    }
}
