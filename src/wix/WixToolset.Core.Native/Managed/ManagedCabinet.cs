// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Managed
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using WixToolset.Data;

    /// <summary>
    /// Cabinet creation, enumeration and extraction without wixnative.exe (FCI/FDI), so cabinets can be
    /// built and read on any platform. Supports uncompressed, MSZIP and LZX cabinets in a single folder.
    /// </summary>
    internal static class ManagedCabinet
    {
        private const string EnvironmentVariableName = "WIX_MANAGED_CAB";
        private const int BlockSize = 32768;
        private const ushort UtfNameAttribute = 0x80;
        private const int HeaderSize = 36;

        /// <summary>
        /// True when cabinets are handled by managed code: WIX_MANAGED_CAB if set, otherwise everywhere but Windows.
        /// </summary>
        internal static bool Enabled
        {
            get
            {
                var value = Environment.GetEnvironmentVariable(EnvironmentVariableName)?.Trim();
                if (!String.IsNullOrEmpty(value))
                {
                    return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }

                return !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            }
        }

        internal static IReadOnlyCollection<CabinetCreated> Create(string cabPath, IEnumerable<CabinetCompressFile> files, CompressionLevel level, int maxSizeInMB)
        {
            var fullPath = Path.GetFullPath(cabPath);
            var cabName = Path.GetFileName(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);

            var entries = new List<FileEntry>();
            var blockStarts = new List<long>();
            var blockLengths = new List<int>();
            var blockUncompressed = new List<int>();
            // Low, medium and high use LZX with 15, 18 and 21 bit windows, like the native cabinet code.
            var fileList = files.ToList();
            var totalBytes = fileList.Sum(f => new FileInfo(f.Path).Exists ? new FileInfo(f.Path).Length : 0);
            var compression = CabCompression.For(level, totalBytes);

            long uncompressedOffset = 0;
            var buffer = new byte[BlockSize];
            var buffered = 0;

            // Data blocks are written to a temporary file so large cabinets do not live in memory.
            var tempData = Path.Combine(directory, cabName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var firstToken = String.Empty;
            var created = new List<CabinetCreated>();

            try
            {
                using (var dataStream = new FileStream(tempData, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    foreach (var file in fileList)
                    {
                        if (entries.Count == 0)
                        {
                            firstToken = file.Token;
                        }

                        var info = new FileInfo(file.Path);
                        if (!info.Exists)
                        {
                            throw new FileNotFoundException("Cannot add file to cabinet.", file.Path);
                        }

                        if (info.Length > Int32.MaxValue || uncompressedOffset + info.Length > UInt32.MaxValue)
                        {
                            throw new InvalidDataException("The file is too large for a cabinet: " + file.Path);
                        }

                        var entry = new FileEntry
                        {
                            Name = file.Token,
                            Size = (uint)info.Length,
                            Offset = (uint)uncompressedOffset,
                        };
                        ToDosDateTime(info.LastWriteTime, out entry.Date, out entry.Time);
                        entry.Attributes = GetAttributes(info);
                        entry.NameBytes = Encoding.UTF8.GetBytes(entry.Name);
                        entry.IsUtf = entry.NameBytes.Length != entry.Name.Length;
                        entries.Add(entry);

                        using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            int read;
                            while ((read = input.Read(buffer, buffered, buffer.Length - buffered)) > 0)
                            {
                                buffered += read;
                                uncompressedOffset += read;

                                if (buffered == buffer.Length)
                                {
                                    AddBlock(dataStream, buffer, buffered, compression, blockStarts, blockLengths, blockUncompressed);
                                    buffered = 0;
                                }
                            }
                        }
                    }

                    if (buffered > 0)
                    {
                        AddBlock(dataStream, buffer, buffered, compression, blockStarts, blockLengths, blockUncompressed);
                    }

                    // Decide which blocks go in which cabinet. A cabinet is full when adding the next block would
                    // take it past the maximum size; a file can continue into the next cabinet.
                    var limit = maxSizeInMB > 0 ? (long)maxSizeInMB * 1024 * 1024 : Int64.MaxValue;
                    var ranges = PlanCabinets(entries, blockLengths, blockUncompressed, limit);
                    if (ranges.Count > 676 + 1)
                    {
                        throw new NotSupportedException("Cannot split a cabinet more than 676 times.");
                    }

                    var names = new List<string>();
                    names.Add(cabName);
                    for (var k = 1; k < ranges.Count; k++)
                    {
                        names.Add(SplitCabinetName(cabName, k));
                    }

                    var setId = (ushort)(ranges.Count > 1 ? 0x1234 : 0);
                    for (var k = 0; k < ranges.Count; k++)
                    {
                        var target = Path.Combine(directory, names[k]);
                        WriteCabinet(target, dataStream, entries, blockStarts, blockLengths, blockUncompressed, ranges, k, names, compression, setId);
                        created.Add(new CabinetCreated(names[k], firstToken));
                    }
                }
            }
            finally
            {
                try
                {
                    File.Delete(tempData);
                }
                catch (IOException)
                {
                }
            }

            return created;
        }

        private static void AddBlock(Stream dataStream, byte[] buffer, int count, CabCompression compression, List<long> starts, List<int> lengths, List<int> uncompressed)
        {
            var start = dataStream.Position;
            WriteBlock(dataStream, buffer, count, compression);
            starts.Add(start);
            lengths.Add((int)(dataStream.Position - start));
            uncompressed.Add(count);
        }

        // Returns, for each cabinet, the first block it holds and the block after its last.
        private static List<int[]> PlanCabinets(List<FileEntry> entries, List<int> blockLengths, List<int> blockUncompressed, long limit)
        {
            var ranges = new List<int[]>();
            var blockCount = blockLengths.Count;
            if (blockCount == 0)
            {
                ranges.Add(new[] { 0, 0 });
                return ranges;
            }

            // Uncompressed start of each block.
            var uncompressedStart = new long[blockCount + 1];
            for (var i = 0; i < blockCount; i++)
            {
                uncompressedStart[i + 1] = uncompressedStart[i] + blockUncompressed[i];
            }

            var first = 0;
            while (first < blockCount)
            {
                var end = first + 1;
                while (end < blockCount)
                {
                    if (EstimateSize(entries, blockLengths, uncompressedStart, first, end + 1, blockCount) > limit)
                    {
                        break;
                    }

                    end++;
                }

                ranges.Add(new[] { first, end });
                first = end;
            }

            return ranges;
        }

        private static long EstimateSize(List<FileEntry> entries, List<int> blockLengths, long[] uncompressedStart, int first, int end, int blockCount)
        {
            // Header, the names of the neighbouring cabinets, one folder, the file entries and the data.
            long size = HeaderSize + 256 + 8;
            for (var i = first; i < end; i++)
            {
                size += blockLengths[i];
            }

            var from = uncompressedStart[first];
            var to = uncompressedStart[end];
            foreach (var entry in entries)
            {
                if (Intersects(entry, from, to, end == blockCount))
                {
                    size += 16 + entry.NameBytes.Length + 1;
                }
            }

            return size;
        }

        private static bool Intersects(FileEntry entry, long from, long to, bool last)
        {
            long start = entry.Offset;
            long finish = start + entry.Size;
            if (entry.Size == 0)
            {
                return (start >= from && start < to) || (last && start == to);
            }

            return start < to && finish > from;
        }

        private static string SplitCabinetName(string firstCabName, int index)
        {
            var stem = firstCabName.Length > 4 ? firstCabName.Substring(0, firstCabName.Length - 4) : firstCabName;
            if (index <= 26)
            {
                return stem + (char)('a' - 1 + index) + ".cab";
            }

            var second = index % 26;
            var first = (index - second) / 26;
            if (second == 0)
            {
                second = 26;
                first--;
            }

            return stem + (char)('a' - 1 + first) + (char)('a' - 1 + second) + ".cab";
        }

        private static void WriteCabinet(string target, Stream dataStream, List<FileEntry> entries, List<long> blockStarts, List<int> blockLengths, List<int> blockUncompressed, List<int[]> ranges, int index, List<string> names, CabCompression compression, ushort setId)
        {
            var firstBlock = ranges[index][0];
            var endBlock = ranges[index][1];
            var blockCount = endBlock - firstBlock;
            var hasPrevious = index > 0;
            var hasNext = index < ranges.Count - 1;
            var isLast = !hasNext;

            long from = 0;
            for (var i = 0; i < firstBlock; i++)
            {
                from += blockUncompressed[i];
            }

            long to = from;
            for (var i = firstBlock; i < endBlock; i++)
            {
                to += blockUncompressed[i];
            }

            var inCabinet = new List<FileEntry>();
            foreach (var entry in entries)
            {
                if (Intersects(entry, from, to, isLast))
                {
                    inCabinet.Add(entry);
                }
            }

            var previousName = hasPrevious ? Encoding.ASCII.GetBytes(names[index - 1]) : null;
            var nextName = hasNext ? Encoding.ASCII.GetBytes(names[index + 1]) : null;
            var disk = new byte[] { (byte)'1', 0 };

            var folderCount = blockCount > 0 ? 1 : 0;
            var flags = (hasPrevious ? 1 : 0) | (hasNext ? 2 : 0);
            var headerExtra = (hasPrevious ? previousName.Length + 1 + disk.Length : 0) + (hasNext ? nextName.Length + 1 + disk.Length : 0);
            var filesOffset = HeaderSize + headerExtra + (folderCount * 8);

            long fileBytes = 0;
            foreach (var entry in inCabinet)
            {
                fileBytes += 16 + entry.NameBytes.Length + 1;
            }

            long dataLength = 0;
            for (var i = firstBlock; i < endBlock; i++)
            {
                dataLength += blockLengths[i];
            }

            var dataOffset = filesOffset + fileBytes;
            var total = dataOffset + dataLength;
            if (total > UInt32.MaxValue)
            {
                throw new NotSupportedException("The cabinet would be larger than 4 GB.");
            }

            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(new byte[] { (byte)'M', (byte)'S', (byte)'C', (byte)'F' });
                writer.Write(0u);                       // reserved1
                writer.Write((uint)total);              // cbCabinet
                writer.Write(0u);                       // reserved2
                writer.Write((uint)filesOffset);        // coffFiles
                writer.Write(0u);                       // reserved3
                writer.Write((byte)3);                  // versionMinor
                writer.Write((byte)1);                  // versionMajor
                writer.Write((ushort)folderCount);      // cFolders
                writer.Write((ushort)inCabinet.Count);  // cFiles
                writer.Write((ushort)flags);            // flags
                writer.Write(setId);                    // setID
                writer.Write((ushort)index);            // iCabinet

                if (hasPrevious)
                {
                    writer.Write(previousName);
                    writer.Write((byte)0);
                    writer.Write(disk);
                }

                if (hasNext)
                {
                    writer.Write(nextName);
                    writer.Write((byte)0);
                    writer.Write(disk);
                }

                if (folderCount == 1)
                {
                    writer.Write((uint)dataOffset);                     // coffCabStart
                    writer.Write((ushort)blockCount);                   // cCFData
                    writer.Write(compression.TypeCompress);                 // typeCompress
                }

                foreach (var entry in inCabinet)
                {
                    long start = entry.Offset;
                    long finish = start + entry.Size;
                    var continuedFromPrevious = start < from;
                    var continuedToNext = !isLast && finish > to;
                    ushort folder = 0;
                    if (continuedFromPrevious && continuedToNext)
                    {
                        folder = 0xffff;
                    }
                    else if (continuedFromPrevious)
                    {
                        folder = 0xfffd;
                    }
                    else if (continuedToNext)
                    {
                        folder = 0xfffe;
                    }

                    writer.Write(entry.Size);
                    writer.Write(entry.Offset);
                    writer.Write(folder);
                    writer.Write(entry.Date);
                    writer.Write(entry.Time);
                    writer.Write((ushort)(entry.Attributes | (entry.IsUtf ? UtfNameAttribute : 0)));
                    writer.Write(entry.NameBytes);
                    writer.Write((byte)0);
                }

                writer.Flush();

                var copy = new byte[81920];
                for (var i = firstBlock; i < endBlock; i++)
                {
                    dataStream.Position = blockStarts[i];
                    var remaining = blockLengths[i];
                    while (remaining > 0)
                    {
                        var read = dataStream.Read(copy, 0, Math.Min(copy.Length, remaining));
                        if (read <= 0)
                        {
                            throw new EndOfStreamException();
                        }

                        output.Write(copy, 0, read);
                        remaining -= read;
                    }
                }
            }
        }

        internal static List<CabinetFileInfo> Enumerate(string cabPath)
        {
            var result = new List<CabinetFileInfo>();
            using (var stream = new FileStream(cabPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var cab = ReadHeader(stream);
                foreach (var file in cab.Files)
                {
                    result.Add(new CabinetFileInfo(file.Name, checked((int)file.Size), file.Date, file.Time));
                }
            }

            return result;
        }

        internal static IEnumerable<string> Extract(string cabPath, string outputFolder)
        {
            var extracted = new List<string>();
            Directory.CreateDirectory(outputFolder);

            using (var stream = new FileStream(cabPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var cab = ReadHeader(stream);
                var folderData = new Dictionary<int, byte[]>();

                foreach (var file in cab.Files)
                {
                    if (file.Name.IndexOfAny(new[] { '/', '\\' }) >= 0 || file.Name == ".." || file.Name == ".")
                    {
                        throw new InvalidDataException("The cabinet contains a file name with a path: " + file.Name);
                    }

                    byte[] data;
                    if (!folderData.TryGetValue(file.Folder, out data))
                    {
                        data = ReadFolder(stream, cab, file.Folder);
                        folderData.Clear();
                        folderData.Add(file.Folder, data);
                    }

                    var target = Path.Combine(outputFolder, file.Name);
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        output.Write(data, checked((int)file.Offset), checked((int)file.Size));
                    }

                    try
                    {
                        var stamp = FromDosDateTime(file.Date, file.Time);
                        File.SetCreationTime(target, stamp);
                        File.SetLastWriteTime(target, stamp);
                    }
                    catch (ArgumentException)
                    {
                    }

                    extracted.Add(file.Name);
                }
            }

            return extracted;
        }

        internal static void ToDosDateTime(DateTime local, out ushort date, out ushort time)
        {
            if (local.Year < 1980)
            {
                date = 0;
                time = 0;
                return;
            }

            date = (ushort)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day);
            time = (ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
        }

        private static DateTime FromDosDateTime(int date, int time)
        {
            var year = 1980 + ((date >> 9) & 0x7f);
            var month = Math.Max(1, Math.Min(12, (date >> 5) & 0xf));
            var day = Math.Max(1, Math.Min(DateTime.DaysInMonth(year, month), date & 0x1f));
            var hour = Math.Min(23, (time >> 11) & 0x1f);
            var minute = Math.Min(59, (time >> 5) & 0x3f);
            var second = Math.Min(59, (time & 0x1f) * 2);
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        }

        private static ushort GetAttributes(FileInfo info)
        {
            const int ReadOnly = 0x1;
            const int Hidden = 0x2;
            const int System = 0x4;

            // The native cabinet code leaves the archive bit out of the file entries, so neither does this.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return (ushort)((int)info.Attributes & (ReadOnly | Hidden | System));
            }

            return (ushort)(info.IsReadOnly ? ReadOnly : 0);
        }

        // Writes one CFDATA block.
        private static void WriteBlock(Stream output, byte[] data, int count, CabCompression compression)
        {
            byte[] payload;
            if (compression.Lzx != null)
            {
                payload = compression.Lzx.EncodeFrame(data, count);
            }
            else if (compression.Mszip)
            {
                payload = CompressMszipBlock(data, count, compression.MszipLevel);
            }
            else
            {
                payload = new byte[count];
                Buffer.BlockCopy(data, 0, payload, 0, count);
            }

            var cbData = (ushort)payload.Length;
            var cbUncomp = (ushort)count;
            var checksum = Checksum(payload, payload.Length, 0);
            var sizes = new[] { (byte)(cbData & 0xff), (byte)((cbData >> 8) & 0xff), (byte)(cbUncomp & 0xff), (byte)((cbUncomp >> 8) & 0xff) };
            checksum = Checksum(sizes, 4, checksum);

            var header = new byte[8];
            header[0] = (byte)(checksum & 0xff);
            header[1] = (byte)((checksum >> 8) & 0xff);
            header[2] = (byte)((checksum >> 16) & 0xff);
            header[3] = (byte)((checksum >> 24) & 0xff);
            Buffer.BlockCopy(sizes, 0, header, 4, 4);

            output.Write(header, 0, header.Length);
            output.Write(payload, 0, payload.Length);
        }

        // How the data blocks of the cabinet folder are compressed. The LZX encoder is stateful: it carries its window from block to block.
        private sealed class CabCompression
        {
            private const long LzxSizeLimit = 0x60000000;

            public bool Mszip;
            public System.IO.Compression.CompressionLevel MszipLevel = System.IO.Compression.CompressionLevel.Optimal;
            public LzxEncoder Lzx;
            public ushort TypeCompress;

            public static CabCompression For(CompressionLevel level, long totalBytes)
            {
                var result = new CabCompression();
                if (level == CompressionLevel.None)
                {
                    return result;
                }

                var windowBits = level == CompressionLevel.Low ? 15 : level == CompressionLevel.Medium ? 18 : level == CompressionLevel.High ? 21 : 0;
                if (windowBits == 0 || totalBytes > LzxSizeLimit)
                {
                    // MSZIP was asked for, or the data is too large for the encoder's 32 bit window positions.
                    result.Mszip = true;
                    result.TypeCompress = 1;
                    return result;
                }

                result.Lzx = new LzxEncoder(windowBits, level == CompressionLevel.High);
                result.TypeCompress = (ushort)(3 | (windowBits << 8));
                return result;
            }
        }

        private static byte[] CompressMszipBlock(byte[] data, int count, System.IO.Compression.CompressionLevel level)
        {
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var deflate = new System.IO.Compression.DeflateStream(ms, level, true))
                {
                    deflate.Write(data, 0, count);
                }

                compressed = ms.ToArray();
            }

            // An incompressible block is stored instead (a single final stored deflate block).
            byte[] deflated;
            if (compressed.Length > count + 5)
            {
                deflated = new byte[count + 5];
                deflated[0] = 0x01;
                deflated[1] = (byte)(count & 0xff);
                deflated[2] = (byte)((count >> 8) & 0xff);
                deflated[3] = (byte)(~count & 0xff);
                deflated[4] = (byte)((~count >> 8) & 0xff);
                Buffer.BlockCopy(data, 0, deflated, 5, count);
            }
            else
            {
                deflated = compressed;
            }

            var result = new byte[deflated.Length + 2];
            result[0] = (byte)'C';
            result[1] = (byte)'K';
            Buffer.BlockCopy(deflated, 0, result, 2, deflated.Length);
            return result;
        }

        // The cabinet checksum: XOR of little-endian 32-bit words, with the trailing bytes folded in big-endian order.
        private static uint Checksum(byte[] data, int count, uint seed)
        {
            var checksum = seed;
            var words = count / 4;
            var i = 0;
            for (var w = 0; w < words; w++, i += 4)
            {
                checksum ^= unchecked((uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24)));
            }

            uint rest = 0;
            switch (count & 3)
            {
                case 3:
                    rest = (uint)((data[i] << 16) | (data[i + 1] << 8) | data[i + 2]);
                    break;
                case 2:
                    rest = (uint)((data[i] << 8) | data[i + 1]);
                    break;
                case 1:
                    rest = data[i];
                    break;
            }

            return checksum ^ rest;
        }

        private static CabinetData ReadHeader(Stream stream)
        {
            var reader = new BinaryReader(stream);
            stream.Position = 0;
            if (reader.ReadUInt32() != 0x4643534d)
            {
                throw new InvalidDataException("Not a cabinet file.");
            }

            reader.ReadUInt32(); // reserved1
            reader.ReadUInt32(); // cbCabinet
            reader.ReadUInt32(); // reserved2
            var filesOffset = reader.ReadUInt32();
            reader.ReadUInt32(); // reserved3
            reader.ReadByte();   // versionMinor
            reader.ReadByte();   // versionMajor
            int folderCount = reader.ReadUInt16();
            int fileCount = reader.ReadUInt16();
            int flags = reader.ReadUInt16();
            reader.ReadUInt16(); // setID
            reader.ReadUInt16(); // iCabinet

            if ((flags & 0x3) != 0)
            {
                throw new NotSupportedException("Cabinets that span several files are not supported by the managed cabinet reader.");
            }

            int reserveFolder = 0;
            int reserveData = 0;
            if ((flags & 0x4) != 0)
            {
                int reserveHeader = reader.ReadUInt16();
                reserveFolder = reader.ReadByte();
                reserveData = reader.ReadByte();
                reader.ReadBytes(reserveHeader);
            }

            var cab = new CabinetData { ReserveData = reserveData };
            for (var i = 0; i < folderCount; i++)
            {
                var folder = new FolderEntry
                {
                    DataOffset = reader.ReadUInt32(),
                    BlockCount = reader.ReadUInt16(),
                    Compression = reader.ReadUInt16(),
                };
                reader.ReadBytes(reserveFolder);
                cab.Folders.Add(folder);
            }

            stream.Position = filesOffset;
            for (var i = 0; i < fileCount; i++)
            {
                var entry = new FileEntry
                {
                    Size = reader.ReadUInt32(),
                    Offset = reader.ReadUInt32(),
                    Folder = reader.ReadUInt16(),
                    Date = reader.ReadUInt16(),
                    Time = reader.ReadUInt16(),
                    Attributes = reader.ReadUInt16(),
                };

                var name = new List<byte>();
                int b;
                while ((b = reader.ReadByte()) != 0)
                {
                    name.Add((byte)b);
                }

                entry.Name = ((entry.Attributes & UtfNameAttribute) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(28591)).GetString(name.ToArray());
                if (entry.Folder >= folderCount)
                {
                    throw new NotSupportedException("The cabinet file continues in another cabinet.");
                }

                cab.Files.Add(entry);
            }

            return cab;
        }

        private static byte[] ReadFolder(Stream stream, CabinetData cab, int folderIndex)
        {
            var folder = cab.Folders[folderIndex];
            var type = folder.Compression & 0xf;
            if (type != 0 && type != 1 && type != 3)
            {
                throw new NotSupportedException("The managed cabinet reader supports uncompressed, MSZIP and LZX cabinets, not compression type " + type + ".");
            }

            stream.Position = folder.DataOffset;
            var reader = new BinaryReader(stream);

            var payloads = new List<byte[]>();
            var sizes = new List<int>();
            long total = 0;
            for (var i = 0; i < folder.BlockCount; i++)
            {
                reader.ReadUInt32(); // checksum
                int cbData = reader.ReadUInt16();
                int cbUncomp = reader.ReadUInt16();
                reader.ReadBytes(cab.ReserveData);
                var payload = reader.ReadBytes(cbData);
                if (payload.Length != cbData)
                {
                    throw new InvalidDataException("The cabinet is truncated.");
                }

                payloads.Add(payload);
                sizes.Add(cbUncomp);
                total += cbUncomp;
            }

            if (total > Int32.MaxValue)
            {
                throw new NotSupportedException("The cabinet folder is larger than 2 GB.");
            }

            if (type == 3)
            {
                var windowBits = (folder.Compression >> 8) & 0x1f;
                var lzx = new LzxDecoder(windowBits, (int)total);
                for (var i = 0; i < payloads.Count; i++)
                {
                    lzx.DecodeFrame(payloads[i], sizes[i]);
                }

                return lzx.Output;
            }

            var output = new MemoryStream();
            var history = new byte[0];
            for (var i = 0; i < payloads.Count; i++)
            {
                var payload = payloads[i];
                byte[] block;
                if (type == 0)
                {
                    block = payload;
                }
                else
                {
                    if (payload.Length < 2 || payload[0] != 'C' || payload[1] != 'K')
                    {
                        throw new InvalidDataException("Invalid MSZIP block.");
                    }

                    block = Inflater.Inflate(payload, 2, history, sizes[i]);
                }

                output.Write(block, 0, block.Length);

                // Each MSZIP block may refer back into the previous 32 KB of output.
                history = block;
            }

            return output.ToArray();
        }

        private sealed class FileEntry
        {
            public string Name;
            public byte[] NameBytes;
            public bool IsUtf;
            public uint Size;
            public uint Offset;
            public int Folder;
            public ushort Date;
            public ushort Time;
            public ushort Attributes;
        }

        private sealed class FolderEntry
        {
            public uint DataOffset;
            public int BlockCount;
            public int Compression;
        }

        private sealed class CabinetData
        {
            public readonly List<FolderEntry> Folders = new List<FolderEntry>();
            public readonly List<FileEntry> Files = new List<FileEntry>();
            public int ReserveData;
        }

        /// <summary>
        /// A small raw-deflate decoder that, unlike DeflateStream, can start with the previous block's output
        /// as its dictionary, which MSZIP requires.
        /// </summary>
        private sealed class Inflater
        {
            private static readonly ushort[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
            private static readonly ushort[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
            private static readonly ushort[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
            private static readonly ushort[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
            private static readonly byte[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

            private readonly byte[] input;
            private readonly byte[] window;
            private readonly int historyLength;
            private int position;
            private int bitBuffer;
            private int bitCount;
            private int outputLength;

            private Inflater(byte[] input, int start, byte[] history, int expected)
            {
                this.input = input;
                this.position = start;
                this.historyLength = history.Length;
                this.window = new byte[history.Length + expected];
                Buffer.BlockCopy(history, 0, this.window, 0, history.Length);
                this.outputLength = history.Length;
            }

            public static byte[] Inflate(byte[] input, int start, byte[] history, int expected)
            {
                var inflater = new Inflater(input, start, history, expected);
                inflater.Run();
                var count = inflater.outputLength - inflater.historyLength;
                var result = new byte[count];
                Buffer.BlockCopy(inflater.window, inflater.historyLength, result, 0, count);
                return result;
            }

            private void Run()
            {
                int last;
                do
                {
                    last = this.Bits(1);
                    var type = this.Bits(2);
                    switch (type)
                    {
                        case 0:
                            this.Stored();
                            break;
                        case 1:
                            this.Codes(FixedLengths(), FixedDistances());
                            break;
                        case 2:
                            this.Dynamic();
                            break;
                        default:
                            throw new InvalidDataException("Invalid deflate block type.");
                    }
                }
                while (last == 0);
            }

            private int Bits(int need)
            {
                var value = this.bitBuffer;
                while (this.bitCount < need)
                {
                    if (this.position >= this.input.Length)
                    {
                        throw new InvalidDataException("Unexpected end of MSZIP data.");
                    }

                    value |= this.input[this.position++] << this.bitCount;
                    this.bitCount += 8;
                }

                this.bitBuffer = value >> need;
                this.bitCount -= need;
                return value & ((1 << need) - 1);
            }

            private void Stored()
            {
                this.bitBuffer = 0;
                this.bitCount = 0;
                if (this.position + 4 > this.input.Length)
                {
                    throw new InvalidDataException("Unexpected end of MSZIP data.");
                }

                var len = this.input[this.position] | (this.input[this.position + 1] << 8);
                this.position += 4;
                if (this.position + len > this.input.Length || this.outputLength + len > this.window.Length)
                {
                    throw new InvalidDataException("Invalid stored block.");
                }

                Buffer.BlockCopy(this.input, this.position, this.window, this.outputLength, len);
                this.position += len;
                this.outputLength += len;
            }

            private void Dynamic()
            {
                var nlen = this.Bits(5) + 257;
                var ndist = this.Bits(5) + 1;
                var ncode = this.Bits(4) + 4;
                if (nlen > 286 || ndist > 30)
                {
                    throw new InvalidDataException("Bad dynamic block counts.");
                }

                var lengths = new short[19];
                for (var i = 0; i < ncode; i++)
                {
                    lengths[CodeLengthOrder[i]] = (short)this.Bits(3);
                }

                var lencode = new Huffman(lengths, 19);
                var index = 0;
                var all = new short[nlen + ndist];
                while (index < nlen + ndist)
                {
                    var symbol = this.Decode(lencode);
                    if (symbol < 16)
                    {
                        all[index++] = (short)symbol;
                    }
                    else
                    {
                        short previous = 0;
                        int repeat;
                        if (symbol == 16)
                        {
                            if (index == 0)
                            {
                                throw new InvalidDataException("Bad repeat.");
                            }

                            previous = all[index - 1];
                            repeat = 3 + this.Bits(2);
                        }
                        else if (symbol == 17)
                        {
                            repeat = 3 + this.Bits(3);
                        }
                        else
                        {
                            repeat = 11 + this.Bits(7);
                        }

                        if (index + repeat > nlen + ndist)
                        {
                            throw new InvalidDataException("Bad repeat length.");
                        }

                        while (repeat-- > 0)
                        {
                            all[index++] = previous;
                        }
                    }
                }

                var litLengths = new short[nlen];
                Array.Copy(all, 0, litLengths, 0, nlen);
                var distLengths = new short[ndist];
                Array.Copy(all, nlen, distLengths, 0, ndist);
                this.Codes(new Huffman(litLengths, nlen), new Huffman(distLengths, ndist));
            }

            private static Huffman FixedLengths()
            {
                var lengths = new short[288];
                for (var i = 0; i < 144; i++) { lengths[i] = 8; }
                for (var i = 144; i < 256; i++) { lengths[i] = 9; }
                for (var i = 256; i < 280; i++) { lengths[i] = 7; }
                for (var i = 280; i < 288; i++) { lengths[i] = 8; }
                return new Huffman(lengths, 288);
            }

            private static Huffman FixedDistances()
            {
                var lengths = new short[30];
                for (var i = 0; i < 30; i++) { lengths[i] = 5; }
                return new Huffman(lengths, 30);
            }

            private void Codes(Huffman lencode, Huffman distcode)
            {
                for (;;)
                {
                    var symbol = this.Decode(lencode);
                    if (symbol < 256)
                    {
                        if (this.outputLength >= this.window.Length)
                        {
                            throw new InvalidDataException("MSZIP block is larger than declared.");
                        }

                        this.window[this.outputLength++] = (byte)symbol;
                    }
                    else if (symbol == 256)
                    {
                        return;
                    }
                    else
                    {
                        symbol -= 257;
                        if (symbol >= 29)
                        {
                            throw new InvalidDataException("Bad length symbol.");
                        }

                        var length = LengthBase[symbol] + this.Bits(LengthExtra[symbol]);
                        var dsym = this.Decode(distcode);
                        if (dsym >= 30)
                        {
                            throw new InvalidDataException("Bad distance symbol.");
                        }

                        var distance = DistBase[dsym] + this.Bits(DistExtra[dsym]);
                        if (distance > this.outputLength || this.outputLength + length > this.window.Length)
                        {
                            throw new InvalidDataException("Bad distance in MSZIP block.");
                        }

                        while (length-- > 0)
                        {
                            this.window[this.outputLength] = this.window[this.outputLength - distance];
                            this.outputLength++;
                        }
                    }
                }
            }

            private int Decode(Huffman h)
            {
                var code = 0;
                var first = 0;
                var index = 0;
                for (var len = 1; len <= 15; len++)
                {
                    code |= this.Bits(1);
                    int count = h.Count[len];
                    if (code - count < first)
                    {
                        return h.Symbol[index + (code - first)];
                    }

                    index += count;
                    first += count;
                    first <<= 1;
                    code <<= 1;
                }

                throw new InvalidDataException("Invalid Huffman code.");
            }

            private sealed class Huffman
            {
                public readonly short[] Count = new short[16];
                public readonly short[] Symbol;

                public Huffman(short[] lengths, int n)
                {
                    this.Symbol = new short[n];
                    for (var s = 0; s < n; s++)
                    {
                        this.Count[lengths[s]]++;
                    }

                    var offs = new short[16];
                    for (var len = 1; len < 15; len++)
                    {
                        offs[len + 1] = (short)(offs[len] + this.Count[len]);
                    }

                    for (var s = 0; s < n; s++)
                    {
                        if (lengths[s] != 0)
                        {
                            this.Symbol[offs[lengths[s]]++] = (short)s;
                        }
                    }
                }
            }
        }
    }
}
