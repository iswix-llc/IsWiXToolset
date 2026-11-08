// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Managed
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// Encoder for LZX data as used in cabinet files. Each cabinet data block (frame) of up to 32 KB is
    /// encoded as one verbatim LZX block, or as an uncompressed block when that is smaller. The encoder keeps
    /// its window, repeated offsets and Huffman tree lengths between frames, exactly as <see cref="LzxDecoder"/> expects.
    /// </summary>
    internal sealed class LzxEncoder
    {
        private const int NumChars = 256;
        private const int MinMatch = 2;
        private const int MaxMatch = 257;
        private const int NumPrimaryLengths = 7;
        private const int NumSecondaryLengths = 249;
        private const int PretreeSymbols = 20;
        private const int FrameSize = 32768;
        private const int HashBits = 16;
        private const int BlockVerbatim = 1;
        private const int BlockUncompressed = 3;

        private readonly int windowSize;
        private readonly int maxOffset;
        private readonly int mainElements;
        private readonly int maxChain;
        private readonly byte[] history;
        private readonly int[] head = new int[1 << HashBits];
        private readonly int[] prev;
        private readonly byte[] mainLengths;
        private readonly byte[] lengthLengths = new byte[NumSecondaryLengths];

        private int historyLength;
        private int baseAbsolute;
        private int r0 = 1;
        private int r1 = 1;
        private int r2 = 1;
        private bool firstFrame = true;

        public LzxEncoder(int windowBits)
        {
            if (windowBits < 15 || windowBits > 21)
            {
                throw new ArgumentOutOfRangeException(nameof(windowBits));
            }

            int slots;
            if (windowBits == 20)
            {
                slots = 42;
            }
            else if (windowBits == 21)
            {
                slots = 50;
            }
            else
            {
                slots = windowBits << 1;
            }

            this.windowSize = 1 << windowBits;
            this.maxOffset = this.windowSize - 3;
            this.mainElements = NumChars + (slots << 3);
            this.mainLengths = new byte[this.mainElements];
            this.history = new byte[this.windowSize + FrameSize + 512];
            this.prev = new int[this.windowSize];
            this.maxChain = windowBits >= 21 ? 64 : windowBits >= 18 ? 32 : 16;

            for (var i = 0; i < this.head.Length; i++)
            {
                this.head[i] = -1;
            }
        }

        /// <summary>Encodes one frame of at most 32768 bytes and returns the compressed cabinet data block.</summary>
        public byte[] EncodeFrame(byte[] data, int count)
        {
            if (count <= 0 || count > FrameSize)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var start = this.Append(data, count);

            var savedR0 = this.r0;
            var savedR1 = this.r1;
            var savedR2 = this.r2;

            var tokenMain = new int[count];
            var tokenLength = new int[count];
            var tokenFooterValue = new int[count];
            var tokenFooterBits = new byte[count];
            var tokens = this.Tokenize(start, count, tokenMain, tokenLength, tokenFooterValue, tokenFooterBits);

            var mainFrequencies = new int[this.mainElements];
            var lengthFrequencies = new int[NumSecondaryLengths];
            for (var t = 0; t < tokens; t++)
            {
                mainFrequencies[tokenMain[t]]++;
                if (tokenLength[t] >= 0)
                {
                    lengthFrequencies[tokenLength[t]]++;
                }
            }

            var newMainLengths = BuildLengths(mainFrequencies, this.mainElements, 16);
            var newLengthLengths = BuildLengths(lengthFrequencies, NumSecondaryLengths, 16);
            var mainCodes = BuildCodes(newMainLengths);
            var lengthCodes = BuildCodes(newLengthLengths);

            var writer = new BitWriter(count + 64);
            if (this.firstFrame)
            {
                writer.Write(0, 1); // No Intel E8 translation.
            }

            writer.Write(BlockVerbatim, 3);
            writer.Write(count >> 8, 16);
            writer.Write(count & 0xff, 8);

            WriteLengths(writer, this.mainLengths, newMainLengths, 0, NumChars);
            WriteLengths(writer, this.mainLengths, newMainLengths, NumChars, this.mainElements);
            WriteLengths(writer, this.lengthLengths, newLengthLengths, 0, NumSecondaryLengths);

            for (var t = 0; t < tokens; t++)
            {
                var main = tokenMain[t];
                writer.Write(mainCodes[main], newMainLengths[main]);
                if (tokenLength[t] >= 0)
                {
                    var lengthSymbol = tokenLength[t];
                    writer.Write(lengthCodes[lengthSymbol], newLengthLengths[lengthSymbol]);
                }

                if (tokenFooterBits[t] > 0)
                {
                    writer.Write(tokenFooterValue[t], tokenFooterBits[t]);
                }
            }

            writer.Flush();

            var uncompressedSize = 4 + 12 + count + (count & 1);
            byte[] result;
            if (writer.Length <= uncompressedSize)
            {
                result = writer.ToArray();
                Array.Copy(newMainLengths, this.mainLengths, this.mainElements);
                Array.Copy(newLengthLengths, this.lengthLengths, NumSecondaryLengths);
            }
            else
            {
                // Incompressible: store the frame and leave the trees and repeated offsets as they were.
                this.r0 = savedR0;
                this.r1 = savedR1;
                this.r2 = savedR2;
                result = this.EncodeUncompressed(data, count);
            }

            this.firstFrame = false;
            return result;
        }

        private static int[] BuildCodes(byte[] lengths)
        {
            var count = new int[18];
            foreach (var length in lengths)
            {
                count[length]++;
            }

            // The canonical assignment used by LzxDecoder: first code of a length is (first code of the previous length + its count) << 1.
            var firstCode = new int[18];
            var running = 0;
            for (var length = 1; length <= 16; length++)
            {
                firstCode[length] = running;
                running = (running + count[length]) << 1;
            }

            var codes = new int[lengths.Length];
            var assigned = new int[18];
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                var length = lengths[symbol];
                if (length != 0)
                {
                    codes[symbol] = firstCode[length] + assigned[length]++;
                }
            }

            return codes;
        }

        // Builds length-limited Huffman code lengths. Trees must be complete (or empty) because strict LZX decoders reject incomplete ones.
        private static byte[] BuildLengths(int[] frequencies, int count, int maxLength)
        {
            var lengths = new byte[count];
            var working = new int[count];
            var used = 0;
            for (var i = 0; i < count; i++)
            {
                working[i] = frequencies[i];
                if (working[i] > 0)
                {
                    used++;
                }
            }

            if (used == 0)
            {
                return lengths;
            }

            if (used == 1)
            {
                for (var i = 0; i < count; i++)
                {
                    if (working[i] > 0)
                    {
                        lengths[i] = 1;
                        lengths[i == 0 ? 1 : 0] = 1;
                        break;
                    }
                }

                return lengths;
            }

            while (true)
            {
                var depth = HuffmanDepths(working, count);
                var longest = 0;
                for (var i = 0; i < count; i++)
                {
                    if (depth[i] > longest)
                    {
                        longest = depth[i];
                    }
                }

                if (longest <= maxLength)
                {
                    for (var i = 0; i < count; i++)
                    {
                        lengths[i] = (byte)depth[i];
                    }

                    return lengths;
                }

                for (var i = 0; i < count; i++)
                {
                    if (working[i] > 0)
                    {
                        working[i] = (working[i] + 1) >> 1;
                    }
                }
            }
        }

        private static int[] HuffmanDepths(int[] frequencies, int count)
        {
            var symbols = new List<int>();
            for (var i = 0; i < count; i++)
            {
                if (frequencies[i] > 0)
                {
                    symbols.Add(i);
                }
            }

            symbols.Sort((a, b) => frequencies[a] != frequencies[b] ? frequencies[a].CompareTo(frequencies[b]) : a.CompareTo(b));

            var leaves = symbols.Count;
            var nodes = (2 * leaves) - 1;
            var weight = new long[nodes];
            var parent = new int[nodes];
            for (var i = 0; i < leaves; i++)
            {
                weight[i] = frequencies[symbols[i]];
            }

            var leaf = 0;
            var internalNode = leaves;
            var next = leaves;
            for (var merge = 0; merge < leaves - 1; merge++)
            {
                var pair = new int[2];
                for (var k = 0; k < 2; k++)
                {
                    if (leaf < leaves && (internalNode >= next || weight[leaf] <= weight[internalNode]))
                    {
                        pair[k] = leaf++;
                    }
                    else
                    {
                        pair[k] = internalNode++;
                    }
                }

                weight[next] = weight[pair[0]] + weight[pair[1]];
                parent[pair[0]] = next;
                parent[pair[1]] = next;
                next++;
            }

            var nodeDepth = new int[nodes];
            for (var i = nodes - 2; i >= 0; i--)
            {
                nodeDepth[i] = nodeDepth[parent[i]] + 1;
            }

            var depths = new int[count];
            for (var i = 0; i < leaves; i++)
            {
                depths[symbols[i]] = nodeDepth[i];
            }

            return depths;
        }

        private static void WriteLengths(BitWriter writer, byte[] previous, byte[] current, int first, int last)
        {
            var tokenSymbol = new List<int>();
            var tokenExtraBits = new List<int>();
            var tokenExtraValue = new List<int>();
            var tokenDelta = new List<int>();

            var x = first;
            while (x < last)
            {
                var run = 1;
                while (x + run < last && current[x + run] == current[x])
                {
                    run++;
                }

                if (current[x] == 0 && run >= 20)
                {
                    run = Math.Min(run, 51);
                    tokenSymbol.Add(18);
                    tokenExtraBits.Add(5);
                    tokenExtraValue.Add(run - 20);
                    tokenDelta.Add(-1);
                }
                else if (current[x] == 0 && run >= 4)
                {
                    run = Math.Min(run, 19);
                    tokenSymbol.Add(17);
                    tokenExtraBits.Add(4);
                    tokenExtraValue.Add(run - 4);
                    tokenDelta.Add(-1);
                }
                else if (run >= 4)
                {
                    run = Math.Min(run, 5);
                    tokenSymbol.Add(19);
                    tokenExtraBits.Add(1);
                    tokenExtraValue.Add(run - 4);
                    tokenDelta.Add((previous[x] - current[x] + 17) % 17);
                }
                else
                {
                    run = 1;
                    tokenSymbol.Add((previous[x] - current[x] + 17) % 17);
                    tokenExtraBits.Add(0);
                    tokenExtraValue.Add(0);
                    tokenDelta.Add(-1);
                }

                x += run;
            }

            var frequencies = new int[PretreeSymbols];
            for (var t = 0; t < tokenSymbol.Count; t++)
            {
                frequencies[tokenSymbol[t]]++;
                if (tokenDelta[t] >= 0)
                {
                    frequencies[tokenDelta[t]]++;
                }
            }

            var lengths = BuildLengths(frequencies, PretreeSymbols, 15);
            var codes = BuildCodes(lengths);
            for (var i = 0; i < PretreeSymbols; i++)
            {
                writer.Write(lengths[i], 4);
            }

            for (var t = 0; t < tokenSymbol.Count; t++)
            {
                var symbol = tokenSymbol[t];
                writer.Write(codes[symbol], lengths[symbol]);
                if (tokenExtraBits[t] > 0)
                {
                    writer.Write(tokenExtraValue[t], tokenExtraBits[t]);
                }

                if (tokenDelta[t] >= 0)
                {
                    var delta = tokenDelta[t];
                    writer.Write(codes[delta], lengths[delta]);
                }
            }
        }

        private static int PositionSlot(int formatted, int slots)
        {
            var low = 0;
            var high = slots - 1;
            while (low < high)
            {
                var middle = (low + high + 1) >> 1;
                if (LzxDecoder.PositionBase[middle] <= formatted)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return low;
        }

        private int Append(byte[] data, int count)
        {
            if (this.historyLength + count > this.history.Length)
            {
                var drop = this.historyLength - this.windowSize;
                Buffer.BlockCopy(this.history, drop, this.history, 0, this.windowSize);
                this.historyLength = this.windowSize;
                this.baseAbsolute += drop;
            }

            var start = this.historyLength;
            Buffer.BlockCopy(data, 0, this.history, start, count);
            this.historyLength += count;
            return start;
        }

        private int Tokenize(int start, int count, int[] tokenMain, int[] tokenLength, int[] tokenFooterValue, byte[] tokenFooterBits)
        {
            var slots = (this.mainElements - NumChars) >> 3;
            var end = start + count;
            var tokens = 0;
            var position = start;

            var haveNext = false;
            var nextLength = 0;
            var nextOffset = 0;

            while (position < end)
            {
                int length;
                int offset;
                if (haveNext)
                {
                    length = nextLength;
                    offset = nextOffset;
                    haveNext = false;
                }
                else
                {
                    this.FindMatch(position, end, out length, out offset);
                }

                if (length >= 3 && position + 1 < end && length < 32)
                {
                    this.FindMatch(position + 1, end, out var length2, out var offset2);
                    if (length2 > length)
                    {
                        // A longer match starts one byte later: emit this byte as a literal.
                        this.Insert(position);
                        tokenMain[tokens] = this.history[position];
                        tokenLength[tokens] = -1;
                        tokenFooterBits[tokens] = 0;
                        tokens++;
                        position++;
                        haveNext = true;
                        nextLength = length2;
                        nextOffset = offset2;
                        continue;
                    }
                }

                if (length < 3)
                {
                    this.Insert(position);
                    tokenMain[tokens] = this.history[position];
                    tokenLength[tokens] = -1;
                    tokenFooterBits[tokens] = 0;
                    tokens++;
                    position++;
                    continue;
                }

                int slot;
                var footerBits = 0;
                var footerValue = 0;
                if (offset == this.r0)
                {
                    slot = 0;
                }
                else if (offset == this.r1)
                {
                    slot = 1;
                    this.r1 = this.r0;
                    this.r0 = offset;
                }
                else if (offset == this.r2)
                {
                    slot = 2;
                    this.r2 = this.r0;
                    this.r0 = offset;
                }
                else
                {
                    var formatted = offset + 2;
                    slot = PositionSlot(formatted, slots);
                    footerBits = LzxDecoder.ExtraBits[slot];
                    footerValue = formatted - LzxDecoder.PositionBase[slot];
                    this.r2 = this.r1;
                    this.r1 = this.r0;
                    this.r0 = offset;
                }

                var lengthCode = length - MinMatch;
                var primary = Math.Min(lengthCode, NumPrimaryLengths);
                tokenMain[tokens] = NumChars + (slot << 3) + primary;
                tokenLength[tokens] = lengthCode >= NumPrimaryLengths ? lengthCode - NumPrimaryLengths : -1;
                tokenFooterValue[tokens] = footerValue;
                tokenFooterBits[tokens] = (byte)footerBits;
                tokens++;

                for (var i = 0; i < length; i++)
                {
                    this.Insert(position + i);
                }

                position += length;
            }

            return tokens;
        }

        private int HashAt(int index)
        {
            // Multiplicative hashing relies on wrapping arithmetic, which a project built with overflow checking would otherwise reject.
            unchecked
            {
                var value = (uint)((this.history[index] << 16) | (this.history[index + 1] << 8) | this.history[index + 2]);
                return (int)((value * 2654435761u) >> (32 - HashBits));
            }
        }

        private void Insert(int index)
        {
            if (index + 2 >= this.historyLength)
            {
                return;
            }

            var absolute = this.baseAbsolute + index;
            var hash = this.HashAt(index);
            this.prev[absolute & (this.windowSize - 1)] = this.head[hash];
            this.head[hash] = absolute;
        }

        private void FindMatch(int index, int end, out int bestLength, out int bestOffset)
        {
            bestLength = 0;
            bestOffset = 0;

            var limit = Math.Min(MaxMatch, end - index);
            if (limit < 3)
            {
                return;
            }

            var available = index; // History index equals the number of bytes before this one in the buffer.

            this.TryOffset(this.r0, index, limit, available, ref bestLength, ref bestOffset);
            this.TryOffset(this.r1, index, limit, available, ref bestLength, ref bestOffset);
            this.TryOffset(this.r2, index, limit, available, ref bestLength, ref bestOffset);

            if (index + 2 >= this.historyLength)
            {
                return;
            }

            var absolute = this.baseAbsolute + index;
            var candidate = this.head[this.HashAt(index)];
            var depth = this.maxChain;
            while (candidate >= this.baseAbsolute && absolute - candidate <= this.maxOffset && depth-- > 0)
            {
                var distance = absolute - candidate;
                this.TryOffset(distance, index, limit, available, ref bestLength, ref bestOffset);
                if (bestLength >= limit)
                {
                    break;
                }

                candidate = this.prev[candidate & (this.windowSize - 1)];
            }

            // Short matches that are far away cost more than literals.
            if (bestLength == 3 && bestOffset > 4096 && bestOffset != this.r0 && bestOffset != this.r1 && bestOffset != this.r2)
            {
                bestLength = 0;
                bestOffset = 0;
            }
        }

        private void TryOffset(int offset, int index, int limit, int available, ref int bestLength, ref int bestOffset)
        {
            if (offset <= 0 || offset > this.maxOffset || offset > available)
            {
                return;
            }

            var source = index - offset;
            var length = 0;
            while (length < limit && this.history[source + length] == this.history[index + length])
            {
                length++;
            }

            if (length > bestLength)
            {
                bestLength = length;
                bestOffset = offset;
            }
        }

        private byte[] EncodeUncompressed(byte[] data, int count)
        {
            var writer = new BitWriter(count + 32);
            if (this.firstFrame)
            {
                writer.Write(0, 1);
            }

            writer.Write(BlockUncompressed, 3);
            writer.Write(count >> 8, 16);
            writer.Write(count & 0xff, 8);
            writer.Flush();
            writer.WriteInt32(this.r0);
            writer.WriteInt32(this.r1);
            writer.WriteInt32(this.r2);
            writer.WriteBytes(data, count);
            if ((count & 1) != 0)
            {
                writer.WriteBytes(new byte[1], 1);
            }

            return writer.ToArray();
        }

        private sealed class BitWriter
        {
            private readonly MemoryStream output;
            private ulong accumulator;
            private int bits;

            public BitWriter(int capacity)
            {
                this.output = new MemoryStream(capacity);
            }

            public int Length
            {
                get { return (int)this.output.Length; }
            }

            public void Write(int value, int count)
            {
                if (count == 0)
                {
                    return;
                }

                this.accumulator = (this.accumulator << count) | ((ulong)(uint)value & ((1UL << count) - 1));
                this.bits += count;
                while (this.bits >= 16)
                {
                    var word = (int)((this.accumulator >> (this.bits - 16)) & 0xffff);
                    this.output.WriteByte((byte)(word & 0xff));
                    this.output.WriteByte((byte)((word >> 8) & 0xff));
                    this.bits -= 16;
                }

                this.accumulator &= (1UL << this.bits) - 1;
            }

            public void Flush()
            {
                if (this.bits > 0)
                {
                    var word = (int)((this.accumulator << (16 - this.bits)) & 0xffff);
                    this.output.WriteByte((byte)(word & 0xff));
                    this.output.WriteByte((byte)((word >> 8) & 0xff));
                    this.bits = 0;
                    this.accumulator = 0;
                }
            }

            public void WriteInt32(int value)
            {
                this.output.WriteByte((byte)(value & 0xff));
                this.output.WriteByte((byte)((value >> 8) & 0xff));
                this.output.WriteByte((byte)((value >> 16) & 0xff));
                this.output.WriteByte((byte)((value >> 24) & 0xff));
            }

            public void WriteBytes(byte[] data, int count)
            {
                this.output.Write(data, 0, count);
            }

            public byte[] ToArray()
            {
                return this.output.ToArray();
            }
        }
    }
}
