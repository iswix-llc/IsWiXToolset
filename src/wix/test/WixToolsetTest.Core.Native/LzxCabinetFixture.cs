// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolsetTest.CoreNative
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using WixInternal.TestSupport;
    using WixToolset.Core.Native;
    using WixToolset.Data;
    using Xunit;

    // These tests switch the managed cabinet code on through an environment variable, so they must not overlap other cabinet tests.
    [Collection("ManagedCabinetEnvironment")]
    [Trait("Platform", "CrossPlatform")]
    public class LzxCabinetFixture
    {
        private const string ManagedCabinetVariable = "WIX_MANAGED_CAB";

        public static IEnumerable<object[]> LevelsAndData()
        {
            foreach (var level in new[] { CompressionLevel.Low, CompressionLevel.Medium, CompressionLevel.High })
            {
                foreach (var name in new[] { "text", "random", "mixed", "zeros", "tiny", "odd", "repeatsfaraway" })
                {
                    yield return new object[] { level, name };
                }
            }
        }

        [Theory]
        [InlineData(CompressionLevel.Low, 0x0f03)]
        [InlineData(CompressionLevel.Medium, 0x1203)]
        [InlineData(CompressionLevel.High, 0x1503)]
        [InlineData(CompressionLevel.Mszip, 0x0001)]
        [InlineData(CompressionLevel.None, 0x0000)]
        public void WritesTheExpectedFolderCompressionType(CompressionLevel level, int expected)
        {
            using (new ManagedCabinetScope("1"))
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var cabPath = Path.Combine(folder, "type.cab");
                var source = Path.Combine(folder, "a.txt");
                File.WriteAllBytes(source, MakeData("text"));

                new Cabinet(cabPath).Compress(new[] { new CabinetCompressFile(source, "a.txt") }, level);

                var bytes = File.ReadAllBytes(cabPath);
                const int folderOffset = 36; // A single cabinet has its CFFOLDER right after the 36 byte header.
                var typeCompress = BitConverter.ToUInt16(bytes, folderOffset + 6);
                Assert.Equal(expected, typeCompress);
            }
        }

        [Theory]
        [MemberData(nameof(LevelsAndData))]
        public void CanRoundTripLzx(CompressionLevel level, string dataName)
        {
            using (new ManagedCabinetScope("1"))
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var source = Path.Combine(folder, "in.bin");
                var data = MakeData(dataName);
                File.WriteAllBytes(source, data);

                var cabPath = Path.Combine(folder, "out.cab");
                new Cabinet(cabPath).Compress(new[] { new CabinetCompressFile(source, "file.bin") }, level);

                var extractTo = Path.Combine(folder, "extracted");
                new Cabinet(cabPath).Extract(extractTo);

                Assert.Equal(data, File.ReadAllBytes(Path.Combine(extractTo, "file.bin")));
            }
        }

        [Fact]
        public void CompressesRepetitiveDataBetterThanMszip()
        {
            using (new ManagedCabinetScope("1"))
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var source = Path.Combine(folder, "in.txt");
                File.WriteAllBytes(source, MakeData("text"));

                var sizes = new Dictionary<CompressionLevel, long>();
                foreach (var level in new[] { CompressionLevel.None, CompressionLevel.Mszip, CompressionLevel.High })
                {
                    var cabPath = Path.Combine(folder, level + ".cab");
                    new Cabinet(cabPath).Compress(new[] { new CabinetCompressFile(source, "in.txt") }, level);
                    sizes[level] = new FileInfo(cabPath).Length;
                }

                Assert.True(sizes[CompressionLevel.Mszip] < sizes[CompressionLevel.None] / 2, "MSZIP should shrink text.");
                Assert.True(sizes[CompressionLevel.High] < sizes[CompressionLevel.Mszip], "LZX should beat MSZIP on repetitive text: " + sizes[CompressionLevel.High] + " vs " + sizes[CompressionLevel.Mszip]);
            }
        }

        [Fact]
        public void CanRoundTripSeveralFilesInOneLzxFolder()
        {
            using (new ManagedCabinetScope("1"))
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var names = new[] { "text", "random", "tiny", "zeros", "mixed" };
                var files = new List<CabinetCompressFile>();
                foreach (var name in names)
                {
                    var path = Path.Combine(folder, name + ".src");
                    File.WriteAllBytes(path, MakeData(name));
                    files.Add(new CabinetCompressFile(path, name + ".dat"));
                }

                var cabPath = Path.Combine(folder, "many.cab");
                new Cabinet(cabPath).Compress(files, CompressionLevel.High);

                var extractTo = Path.Combine(folder, "extracted");
                new Cabinet(cabPath).Extract(extractTo);

                foreach (var name in names)
                {
                    Assert.Equal(MakeData(name), File.ReadAllBytes(Path.Combine(extractTo, name + ".dat")));
                }
            }
        }

        // The managed reader does not read cabinets split over several files, so extraction of the split set is checked with the native code on Windows.
        [Fact]
        public void CanSplitLzxAcrossCabinets()
        {
            using (var fs = new DisposableFileSystem())
            {
                var folder = fs.GetFolder(true);
                var source = Path.Combine(folder, "big.src");
                var data = MakeData("bigalphabet");
                File.WriteAllBytes(source, data);

                var cabPath = Path.Combine(folder, "split.cab");
                IReadOnlyCollection<CabinetCreated> created;
                using (new ManagedCabinetScope("1"))
                {
                    created = new Cabinet(cabPath).Compress(new[] { new CabinetCompressFile(source, "big.dat") }, CompressionLevel.High, maxSize: 1);
                }

                Assert.True(created.Count > 1, "The data should have been split over more than one cabinet.");
                foreach (var cabinet in created)
                {
                    Assert.True(File.Exists(Path.Combine(folder, cabinet.CabinetName)), cabinet.CabinetName);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var extractTo = Path.Combine(folder, "extracted");
                    Directory.CreateDirectory(extractTo);
                    using (new ManagedCabinetScope("0"))
                    {
                        new Cabinet(cabPath).Extract(extractTo);
                    }

                    Assert.True(data.SequenceEqual(File.ReadAllBytes(Path.Combine(extractTo, "big.dat"))), "Native extraction of the split set differs.");
                }
            }
        }

        [Fact]
        public void WindowsCabinetCodeCanExtractManagedLzxCabinets()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            foreach (var level in new[] { CompressionLevel.Low, CompressionLevel.Medium, CompressionLevel.High })
            {
                foreach (var dataName in new[] { "text", "random", "mixed", "repeatsfaraway", "odd" })
                {
                    using (var fs = new DisposableFileSystem())
                    {
                        var folder = fs.GetFolder(true);
                        var source = Path.Combine(folder, "in.bin");
                        var data = MakeData(dataName);
                        File.WriteAllBytes(source, data);

                        var cabPath = Path.Combine(folder, "out.cab");
                        using (new ManagedCabinetScope("1"))
                        {
                            new Cabinet(cabPath).Compress(new[] { new CabinetCompressFile(source, "file.bin") }, level);
                        }

                        // Extract with the native (FDI) code, which is the reference LZX decoder.
                        var extractTo = Path.Combine(folder, "extracted");
                        Directory.CreateDirectory(extractTo); // The native extractor does not create its output folder.
                        using (new ManagedCabinetScope("0"))
                        {
                            new Cabinet(cabPath).Extract(extractTo);
                        }

                        Assert.True(data.SequenceEqual(File.ReadAllBytes(Path.Combine(extractTo, "file.bin"))), "Native extraction differs for " + level + " / " + dataName);
                    }
                }
            }
        }

        private static byte[] MakeData(string name)
        {
            switch (name)
            {
                case "text":
                    return Text(300000, 1);
                case "odd":
                    return Text(40001, 2);
                case "random":
                    return Random(100000, 3, 256);
                case "bigalphabet":
                    return Random(3 * 1024 * 1024, 4, 24);
                case "zeros":
                    return new byte[70000];
                case "tiny":
                    return new byte[] { 0x42 };
                case "mixed":
                    {
                        var parts = new List<byte>();
                        parts.AddRange(Text(30000, 5));
                        parts.AddRange(Random(20000, 6, 256));
                        parts.AddRange(new byte[9000]);
                        parts.AddRange(Random(40000, 7, 8));
                        parts.AddRange(Text(60000, 8));
                        parts.AddRange(new byte[] { 0xe8, 0x01, 0x02, 0x03, 0x04, 0xe8, 0xff, 0xff, 0xff, 0xff });
                        return parts.ToArray();
                    }

                case "repeatsfaraway":
                    {
                        // A random block repeated so matches reach back about a megabyte, and the window slides.
                        var block = Random(1200000, 9, 256);
                        var data = new byte[block.Length * 3];
                        for (var i = 0; i < 3; i++)
                        {
                            Buffer.BlockCopy(block, 0, data, i * block.Length, block.Length);
                        }

                        return data;
                    }

                default:
                    throw new ArgumentException(name);
            }
        }

        private static byte[] Random(int length, int seed, int alphabet)
        {
            var random = new System.Random(seed);
            var data = new byte[length];
            for (var i = 0; i < length; i++)
            {
                data[i] = (byte)random.Next(alphabet);
            }

            return data;
        }

        private static byte[] Text(int length, int seed)
        {
            var words = new[] { "installer", "package", "component", "feature", "directory", "registry", "the", "of", "and", "file", "WiX", "toolset", "cabinet", "compress", "window", "\r\n" };
            var random = new System.Random(seed);
            var builder = new StringBuilder();
            while (builder.Length < length)
            {
                builder.Append(words[random.Next(words.Length)]).Append(' ');
            }

            return Encoding.ASCII.GetBytes(builder.ToString(0, length));
        }

        private sealed class ManagedCabinetScope : IDisposable
        {
            private readonly string previous;

            public ManagedCabinetScope(string value)
            {
                this.previous = Environment.GetEnvironmentVariable(ManagedCabinetVariable);
                Environment.SetEnvironmentVariable(ManagedCabinetVariable, value);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(ManagedCabinetVariable, this.previous);
            }
        }
    }
}
