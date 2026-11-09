// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Dtf.Resources
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Managed reader and writer for the resource directory of PE files. Used instead of the kernel32 resource
    /// APIs on non-Windows platforms, or on Windows when WIX_MANAGED_RES=1.
    /// </summary>
    internal static class PeResources
    {
        private const uint HighBit = 0x80000000;

        /// <summary>One resource: type, name and language identify it.</summary>
        internal sealed class Item
        {
            public string Type;
            public string Name;
            public int Language;
            public uint CodePage;
            public byte[] Data;

            public string Key
            {
                get { return Normalize(this.Type) + "|" + Normalize(this.Name) + "|" + this.Language.ToString(CultureInfo.InvariantCulture); }
            }
        }

        /// <summary>
        /// Gets whether resources are read and written by managed code. WIX_MANAGED_RES=1 forces it on,
        /// WIX_MANAGED_RES=0 forces it off; otherwise it is on for every platform except Windows.
        /// </summary>
        internal static bool UseManaged
        {
            get
            {
                var value = Environment.GetEnvironmentVariable("WIX_MANAGED_RES");
                if (value != null)
                {
                    value = value.Trim();
                    if (value == "1")
                    {
                        return true;
                    }

                    if (value == "0")
                    {
                        return false;
                    }
                }

                return Environment.OSVersion.Platform != PlatformID.Win32NT;
            }
        }

        /// <summary>Reads every resource from a PE file.</summary>
        internal static List<Item> Read(string file)
        {
            var bytes = File.ReadAllBytes(file);
            try
            {
                return ReadItems(new PeImage(bytes));
            }
            catch (IndexOutOfRangeException)
            {
                throw new IOException("Failed to read resources: the resource directory of '" + file + "' is corrupt.");
            }
            catch (ArgumentException)
            {
                throw new IOException("Failed to read resources: the resource directory of '" + file + "' is corrupt.");
            }
        }

        /// <summary>
        /// Adds or replaces resources in a PE file. An update whose Data is null deletes that resource.
        /// </summary>
        internal static void Write(string file, IEnumerable<Item> updates)
        {
            var bytes = File.ReadAllBytes(file);
            byte[] result;
            try
            {
                result = Rebuild(bytes, updates);
            }
            catch (IndexOutOfRangeException)
            {
                throw new IOException("Failed to write resources: '" + file + "' is not a valid PE file.");
            }

            File.WriteAllBytes(file, result);
        }

        internal static byte[] Rebuild(byte[] bytes, IEnumerable<Item> updates)
        {
            var pe = new PeImage(bytes);
            var resourceIndex = pe.FindResourceSection();
            var rsrc = pe.Sections[resourceIndex];

            // Merge the updates into the existing resources.
            var existing = ReadItems(pe);
            var map = new Dictionary<string, Item>();
            var order = new List<Item>();
            foreach (var item in existing)
            {
                map[item.Key] = item;
                order.Add(item);
            }

            foreach (var update in updates)
            {
                ValidateUpdate(update);
                var key = update.Key;
                Item current;
                if (map.TryGetValue(key, out current))
                {
                    order.Remove(current);
                    map.Remove(key);
                }

                if (update.Data != null)
                {
                    var copy = new Item { Type = update.Type, Name = update.Name, Language = update.Language, CodePage = current != null ? current.CodePage : 0, Data = update.Data };
                    map[key] = copy;
                    order.Add(copy);
                }
            }

            var section = SerializeDirectory(order, rsrc.VirtualAddress);

            // Work out how the layout moves.
            var sectionAlignment = pe.U32(pe.OptionalOffset + 32);
            var fileAlignment = pe.U32(pe.OptionalOffset + 36);
            var used = (uint)section.Length;
            var newRawSize = AlignUp(used, fileAlignment);
            var oldRawSize = rsrc.RawSize;
            var oldRawEnd = rsrc.RawPointer + oldRawSize;

            uint nextVa = 0;
            for (var i = 0; i < pe.Sections.Length; i++)
            {
                var s = pe.Sections[i];
                if (s.VirtualAddress > rsrc.VirtualAddress && (nextVa == 0 || s.VirtualAddress < nextVa))
                {
                    nextVa = s.VirtualAddress;
                }
            }

            var oldVaSpan = nextVa != 0 ? nextVa - rsrc.VirtualAddress : AlignUp(rsrc.VirtualSize, sectionAlignment);
            var newVaSpan = AlignUp(used, sectionAlignment);
            var shiftVa = rsrc.VirtualAddress + oldVaSpan;
            var deltaVa = unchecked((int)newVaSpan - (int)oldVaSpan);
            var deltaRaw = unchecked((int)newRawSize - (int)oldRawSize);

            // Only sections that follow .rsrc in both memory and file can be moved. Anything pointing into them
            // other than the base relocations would have to be rewritten.
            for (var i = 0; i < pe.Sections.Length; i++)
            {
                var s = pe.Sections[i];
                if (i != resourceIndex && s.VirtualAddress > rsrc.VirtualAddress && s.RawSize != 0 && s.RawPointer < oldRawEnd)
                {
                    throw new NotSupportedException("The sections of the PE file are not stored in address order.");
                }
            }

            for (var i = 0; i < pe.DataDirectoryCount; i++)
            {
                if (i == 2 || i == 4 || i == 5)
                {
                    continue;
                }

                var rva = pe.U32(pe.DataDirectoryOffset + (i * 8));
                if (rva >= rsrc.VirtualAddress && rva != 0)
                {
                    throw new NotSupportedException("Data directory " + i + " of the PE file lies at or after the resource section, which is not supported.");
                }
            }

            if (pe.DataDirectoryCount > 5)
            {
                var relocRva = pe.U32(pe.DataDirectoryOffset + 40);
                var relocSize = pe.U32(pe.DataDirectoryOffset + 44);
                if (relocRva != 0)
                {
                    var pos = pe.RvaToOffset(relocRva);
                    var end = pos + (int)relocSize;
                    while (pos + 8 <= end)
                    {
                        var page = pe.U32(pos);
                        var size = pe.U32(pos + 4);
                        if (size < 8)
                        {
                            break;
                        }

                        if (page >= rsrc.VirtualAddress)
                        {
                            throw new NotSupportedException("The PE file has base relocations at or after the resource section, which is not supported.");
                        }

                        pos += (int)size;
                    }
                }
            }

            var result = new byte[bytes.Length + deltaRaw];
            Array.Copy(bytes, 0, result, 0, (int)rsrc.RawPointer);
            Array.Copy(section, 0, result, (int)rsrc.RawPointer, section.Length);
            Array.Copy(bytes, (int)oldRawEnd, result, (int)(rsrc.RawPointer + newRawSize), bytes.Length - (int)oldRawEnd);

            var image = new PeImage(result);

            // Section headers.
            uint lastVa = 0;
            uint lastSize = 0;
            for (var i = 0; i < pe.Sections.Length; i++)
            {
                var s = pe.Sections[i];
                var header = pe.SectionTableOffset + (i * 40);
                if (i == resourceIndex)
                {
                    image.PutU32(header + 8, used);
                    image.PutU32(header + 16, newRawSize);
                }
                else if (s.VirtualAddress > rsrc.VirtualAddress)
                {
                    image.PutU32(header + 12, unchecked((uint)((int)s.VirtualAddress + deltaVa)));
                    if (s.RawPointer != 0)
                    {
                        image.PutU32(header + 20, unchecked((uint)((int)s.RawPointer + deltaRaw)));
                    }
                }

                var va = i == resourceIndex || s.VirtualAddress > rsrc.VirtualAddress ? image.U32(header + 12) : s.VirtualAddress;
                if (va >= lastVa)
                {
                    lastVa = va;
                    lastSize = i == resourceIndex ? used : s.VirtualSize;
                }
            }

            // Data directories: resources, certificate (a file offset) and base relocations.
            image.PutU32(pe.DataDirectoryOffset + 16 + 4, used);
            if (pe.DataDirectoryCount > 4)
            {
                var certificate = pe.U32(pe.DataDirectoryOffset + 32);
                if (certificate != 0 && certificate >= oldRawEnd)
                {
                    image.PutU32(pe.DataDirectoryOffset + 32, unchecked((uint)((int)certificate + deltaRaw)));
                }
            }

            if (pe.DataDirectoryCount > 5)
            {
                var relocRva = pe.U32(pe.DataDirectoryOffset + 40);
                if (relocRva >= shiftVa)
                {
                    image.PutU32(pe.DataDirectoryOffset + 40, unchecked((uint)((int)relocRva + deltaVa)));
                }
            }

            // Optional header totals.
            image.PutU32(pe.OptionalOffset + 56, AlignUp(lastVa + lastSize, sectionAlignment));
            var initializedData = pe.U32(pe.OptionalOffset + 8);
            image.PutU32(pe.OptionalOffset + 8, unchecked((uint)((int)initializedData + deltaRaw)));

            var checksumOffset = pe.OptionalOffset + 64;
            if (pe.U32(checksumOffset) != 0)
            {
                image.PutU32(checksumOffset, 0);
                image.PutU32(checksumOffset, ComputeChecksum(result, checksumOffset));
            }

            return result;
        }

        /// <summary>
        /// Rejects what UpdateResource rejects with ERROR_INVALID_PARAMETER (87): zero-length data (null deletes) and integer
        /// type or name identifiers outside 1..65535.
        /// </summary>
        private static void ValidateUpdate(Item update)
        {
            var invalid = update.Data != null && update.Data.Length == 0;
            foreach (var id in new[] { update.Type, update.Name })
            {
                if (IsNumeric(id))
                {
                    var value = NumericValue(id);
                    invalid |= value == 0 || value > 0xFFFF;
                }
            }

            if (invalid)
            {
                throw new IOException("Failed to save resource. Error: 87");
            }
        }

        internal static uint ComputeChecksum(byte[] data, int checksumOffset)
        {
            ulong sum = 0;
            for (var i = 0; i < data.Length; i += 2)
            {
                if (i == checksumOffset || i == checksumOffset + 2)
                {
                    continue;
                }

                sum += i + 1 < data.Length ? (uint)(data[i] | (data[i + 1] << 8)) : data[i];
            }

            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            return unchecked((uint)sum + (uint)data.Length);
        }

        private static uint AlignUp(uint value, uint alignment)
        {
            if (alignment == 0)
            {
                return value;
            }

            return (value + alignment - 1) / alignment * alignment;
        }

        private static string Normalize(string value)
        {
            return IsNumeric(value) ? value : value.ToUpperInvariant();
        }

        private static bool IsNumeric(string value)
        {
            uint ignored;
            return value.Length > 1 && value[0] == '#' && UInt32.TryParse(value.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out ignored);
        }

        private static uint NumericValue(string value)
        {
            return UInt32.Parse(value.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static int CompareKeys(string a, string b)
        {
            var aNumeric = IsNumeric(a);
            var bNumeric = IsNumeric(b);
            if (aNumeric != bNumeric)
            {
                return aNumeric ? 1 : -1; // named entries come first
            }

            if (aNumeric)
            {
                return NumericValue(a).CompareTo(NumericValue(b));
            }

            return String.CompareOrdinal(a.ToUpperInvariant(), b.ToUpperInvariant());
        }

        private static List<Item> ReadItems(PeImage pe)
        {
            var items = new List<Item>();
            var rva = pe.DataDirectoryCount > 2 ? pe.U32(pe.DataDirectoryOffset + 16) : 0;
            var size = pe.DataDirectoryCount > 2 ? pe.U32(pe.DataDirectoryOffset + 20) : 0;
            if (rva == 0 || size == 0)
            {
                return items;
            }

            var root = pe.RvaToOffset(rva);
            ReadDirectory(pe, root, 0, 0, null, null, items);
            return items;
        }

        private static void ReadDirectory(PeImage pe, int root, uint directoryOffset, int level, string type, string name, List<Item> items)
        {
            var directory = root + (int)directoryOffset;
            var count = pe.U16(directory + 12) + pe.U16(directory + 14);
            for (var i = 0; i < count; i++)
            {
                var entry = directory + 16 + (i * 8);
                var nameField = pe.U32(entry);
                var offsetField = pe.U32(entry + 4);

                string key;
                if ((nameField & HighBit) != 0)
                {
                    var at = root + (int)(nameField & ~HighBit);
                    var length = pe.U16(at);
                    key = Encoding.Unicode.GetString(pe.Bytes, at + 2, length * 2);
                }
                else
                {
                    key = "#" + nameField.ToString(CultureInfo.InvariantCulture);
                }

                if (level == 0)
                {
                    if ((offsetField & HighBit) != 0)
                    {
                        ReadDirectory(pe, root, offsetField & ~HighBit, 1, key, null, items);
                    }
                }
                else if (level == 1)
                {
                    if ((offsetField & HighBit) != 0)
                    {
                        ReadDirectory(pe, root, offsetField & ~HighBit, 2, type, key, items);
                    }
                }
                else if ((offsetField & HighBit) == 0 && (nameField & HighBit) == 0)
                {
                    var data = root + (int)offsetField;
                    var dataRva = pe.U32(data);
                    var dataSize = pe.U32(data + 4);
                    var codePage = pe.U32(data + 8);
                    var bytes = new byte[dataSize];
                    if (dataSize != 0)
                    {
                        Array.Copy(pe.Bytes, pe.RvaToOffset(dataRva), bytes, 0, (int)dataSize);
                    }

                    items.Add(new Item { Type = type, Name = name, Language = (int)nameField, CodePage = codePage, Data = bytes });
                }
            }
        }

        private static byte[] SerializeDirectory(List<Item> items, uint sectionRva)
        {
            // Group into type -> name -> language, each level in the order the loader expects.
            var types = new List<string>();
            var typeNames = new Dictionary<string, List<string>>();
            var nameLanguages = new Dictionary<string, List<Item>>();
            foreach (var item in items)
            {
                var typeKey = Normalize(item.Type);
                if (!typeNames.ContainsKey(typeKey))
                {
                    typeNames[typeKey] = new List<string>();
                    types.Add(item.Type);
                }

                var nameKey = typeKey + "|" + Normalize(item.Name);
                if (!nameLanguages.ContainsKey(nameKey))
                {
                    nameLanguages[nameKey] = new List<Item>();
                    typeNames[typeKey].Add(item.Name);
                }

                nameLanguages[nameKey].Add(item);
            }

            types.Sort(CompareKeys);

            // Pass 1: assign offsets.
            uint offset = 16 + (uint)(types.Count * 8);
            var typeOffsets = new Dictionary<string, uint>();
            var nameOffsets = new Dictionary<string, uint>();
            foreach (var type in types)
            {
                var typeKey = Normalize(type);
                typeOffsets[typeKey] = offset;
                offset += 16 + (uint)(typeNames[typeKey].Count * 8);
            }

            foreach (var type in types)
            {
                var typeKey = Normalize(type);
                typeNames[typeKey].Sort(CompareKeys);
                foreach (var name in typeNames[typeKey])
                {
                    var nameKey = typeKey + "|" + Normalize(name);
                    nameOffsets[nameKey] = offset;
                    nameLanguages[nameKey].Sort(delegate(Item x, Item y) { return ((uint)x.Language).CompareTo((uint)y.Language); });
                    offset += 16 + (uint)(nameLanguages[nameKey].Count * 8);
                }
            }

            var dataEntryOffsets = new Dictionary<Item, uint>();
            foreach (var type in types)
            {
                var typeKey = Normalize(type);
                foreach (var name in typeNames[typeKey])
                {
                    foreach (var item in nameLanguages[typeKey + "|" + Normalize(name)])
                    {
                        dataEntryOffsets[item] = offset;
                        offset += 16;
                    }
                }
            }

            var stringOffsets = new Dictionary<string, uint>();
            var stringList = new List<string>();
            foreach (var type in types)
            {
                var typeKey = Normalize(type);
                if (!IsNumeric(type) && !stringOffsets.ContainsKey(typeKey))
                {
                    stringOffsets[typeKey] = 0;
                    stringList.Add(type);
                }

                foreach (var name in typeNames[typeKey])
                {
                    var nameNormalized = Normalize(name);
                    if (!IsNumeric(name) && !stringOffsets.ContainsKey(nameNormalized))
                    {
                        stringOffsets[nameNormalized] = 0;
                        stringList.Add(name);
                    }
                }
            }

            foreach (var text in stringList)
            {
                stringOffsets[Normalize(text)] = offset;
                offset += 2 + (uint)(text.Length * 2);
                offset = (offset + 1) & ~1u;
            }

            offset = (offset + 7) & ~7u;
            var dataOffsets = new Dictionary<Item, uint>();
            foreach (var type in types)
            {
                var typeKey = Normalize(type);
                foreach (var name in typeNames[typeKey])
                {
                    foreach (var item in nameLanguages[typeKey + "|" + Normalize(name)])
                    {
                        dataOffsets[item] = offset;
                        offset += (uint)item.Data.Length;
                        offset = (offset + 7) & ~7u;
                    }
                }
            }

            // Pass 2: write.
            var buffer = new byte[offset];
            WriteDirectoryHeader(buffer, 0, types, null, typeOffsets, stringOffsets);
            foreach (var type in types)
            {
                var typeKey = Normalize(type);
                WriteDirectoryHeader(buffer, typeOffsets[typeKey], typeNames[typeKey], typeKey, nameOffsets, stringOffsets);
                foreach (var name in typeNames[typeKey])
                {
                    var nameKey = typeKey + "|" + Normalize(name);
                    var languages = nameLanguages[nameKey];
                    var directory = nameOffsets[nameKey];
                    Put16(buffer, directory + 14, (ushort)languages.Count);
                    for (var i = 0; i < languages.Count; i++)
                    {
                        var entry = directory + 16 + (uint)(i * 8);
                        Put32(buffer, entry, (uint)languages[i].Language);
                        Put32(buffer, entry + 4, dataEntryOffsets[languages[i]]);

                        var record = dataEntryOffsets[languages[i]];
                        Put32(buffer, record, sectionRva + dataOffsets[languages[i]]);
                        Put32(buffer, record + 4, (uint)languages[i].Data.Length);
                        Put32(buffer, record + 8, languages[i].CodePage);
                        Array.Copy(languages[i].Data, 0, buffer, dataOffsets[languages[i]], languages[i].Data.Length);
                    }
                }
            }

            foreach (var text in stringList)
            {
                // Like UpdateResource, store string names and types in upper case: the loader looks them up by
                // upper-cased key and fails to find mixed-case entries.
                var at = stringOffsets[Normalize(text)];
                Put16(buffer, at, (ushort)text.Length);
                var encoded = Encoding.Unicode.GetBytes(text.ToUpperInvariant());
                Array.Copy(encoded, 0, buffer, at + 2, encoded.Length);
            }

            // Trim trailing alignment padding so the virtual size is the real size.
            var length = (int)offset;
            if (items.Count != 0)
            {
                uint end = 0;
                foreach (var item in items)
                {
                    var itemEnd = dataOffsets[item] + (uint)item.Data.Length;
                    if (itemEnd > end)
                    {
                        end = itemEnd;
                    }
                }

                length = (int)end;
            }

            var trimmed = new byte[length];
            Array.Copy(buffer, 0, trimmed, 0, length);
            return trimmed;
        }

        private static void WriteDirectoryHeader(byte[] buffer, uint directory, List<string> keys, string parentKey, Dictionary<string, uint> childOffsets, Dictionary<string, uint> stringOffsets)
        {
            var named = 0;
            foreach (var key in keys)
            {
                if (!IsNumeric(key))
                {
                    named++;
                }
            }

            Put16(buffer, directory + 12, (ushort)named);
            Put16(buffer, directory + 14, (ushort)(keys.Count - named));

            for (var i = 0; i < keys.Count; i++)
            {
                var entry = directory + 16 + (uint)(i * 8);
                var key = keys[i];
                var normalized = Normalize(key);
                if (IsNumeric(key))
                {
                    Put32(buffer, entry, NumericValue(key));
                }
                else
                {
                    Put32(buffer, entry, HighBit | stringOffsets[normalized]);
                }

                var childKey = parentKey == null ? normalized : parentKey + "|" + normalized;
                Put32(buffer, entry + 4, HighBit | childOffsets[childKey]);
            }
        }

        private static void Put16(byte[] buffer, uint offset, ushort value)
        {
            buffer[offset] = unchecked((byte)value);
            buffer[offset + 1] = unchecked((byte)(value >> 8));
        }

        private static void Put32(byte[] buffer, uint offset, uint value)
        {
            buffer[offset] = unchecked((byte)value);
            buffer[offset + 1] = unchecked((byte)(value >> 8));
            buffer[offset + 2] = unchecked((byte)(value >> 16));
            buffer[offset + 3] = unchecked((byte)(value >> 24));
        }

        private sealed class SectionInfo
        {
            public uint VirtualSize;
            public uint VirtualAddress;
            public uint RawSize;
            public uint RawPointer;
        }

        private sealed class PeImage
        {
            public readonly byte[] Bytes;
            public int OptionalOffset;
            public int SectionTableOffset;
            public int DataDirectoryOffset;
            public int DataDirectoryCount;
            public SectionInfo[] Sections;

            public PeImage(byte[] bytes)
            {
                this.Bytes = bytes;
                if (bytes.Length < 0x40 || bytes[0] != 'M' || bytes[1] != 'Z')
                {
                    throw new IOException("The file is not a PE file.");
                }

                var pe = (int)this.U32(0x3C);
                if (pe < 0 || pe + 24 > bytes.Length || this.U32(pe) != 0x00004550)
                {
                    throw new IOException("The file is not a PE file.");
                }

                var sectionCount = this.U16(pe + 6);
                var optionalSize = this.U16(pe + 20);
                this.OptionalOffset = pe + 24;
                var magic = this.U16(this.OptionalOffset);
                if (magic != 0x10B && magic != 0x20B)
                {
                    throw new IOException("The file is not a PE file.");
                }

                var countOffset = this.OptionalOffset + (magic == 0x20B ? 108 : 92);
                this.DataDirectoryOffset = countOffset + 4;
                this.DataDirectoryCount = (int)Math.Min(this.U32(countOffset), 16u);
                this.SectionTableOffset = this.OptionalOffset + optionalSize;
                this.Sections = new SectionInfo[sectionCount];
                for (var i = 0; i < sectionCount; i++)
                {
                    var at = this.SectionTableOffset + (i * 40);
                    this.Sections[i] = new SectionInfo
                    {
                        VirtualSize = this.U32(at + 8),
                        VirtualAddress = this.U32(at + 12),
                        RawSize = this.U32(at + 16),
                        RawPointer = this.U32(at + 20),
                    };
                }
            }

            public ushort U16(int offset)
            {
                return (ushort)(this.Bytes[offset] | (this.Bytes[offset + 1] << 8));
            }

            public uint U32(int offset)
            {
                return unchecked((uint)(this.Bytes[offset] | (this.Bytes[offset + 1] << 8) | (this.Bytes[offset + 2] << 16) | (this.Bytes[offset + 3] << 24)));
            }

            public void PutU32(int offset, uint value)
            {
                Put32(this.Bytes, (uint)offset, value);
            }

            public int RvaToOffset(uint rva)
            {
                foreach (var section in this.Sections)
                {
                    var span = Math.Max(section.VirtualSize, section.RawSize);
                    if (rva >= section.VirtualAddress && rva < section.VirtualAddress + span)
                    {
                        return (int)(section.RawPointer + (rva - section.VirtualAddress));
                    }
                }

                throw new IOException("A resource address does not map into any section.");
            }

            public int FindResourceSection()
            {
                var rva = this.DataDirectoryCount > 2 ? this.U32(this.DataDirectoryOffset + 16) : 0;
                if (rva == 0)
                {
                    throw new NotSupportedException("The PE file has no resource section to update.");
                }

                for (var i = 0; i < this.Sections.Length; i++)
                {
                    if (this.Sections[i].VirtualAddress == rva)
                    {
                        return i;
                    }
                }

                throw new NotSupportedException("The resource directory does not start its own section.");
            }
        }
    }
}
