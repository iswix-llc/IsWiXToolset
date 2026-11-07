// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Managed
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Minimal PE reader that extracts the VS_VERSION_INFO resource (file version and language),
    /// as MsiGetFileVersion does. Works on any platform.
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
                return TryParse(data, out version, out language);
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

            var lang = FindTranslation(d, blob, dataSize);
            language = (lang >= 0 ? lang : languageId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        private static int FindTranslation(byte[] d, int blob, int size)
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
                    var valueStart = i + needle.Length + 2; // skip terminating NUL
                    valueStart = blob + (((valueStart - blob) + 3) & ~3);
                    return U16(d, valueStart);
                }
            }

            return -1;
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

        private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);

        private static uint U32(byte[] d, int o) => unchecked((uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)));
    }
}
