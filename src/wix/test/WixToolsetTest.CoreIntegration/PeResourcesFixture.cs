// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolsetTest.CoreIntegration
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection.PortableExecutable;
    using System.Runtime.InteropServices;
    using System.Text;
    using WixInternal.TestSupport;
    using WixToolset.Dtf.Resources;
    using Xunit;

    /// <summary>
    /// Tests for the managed PE resource reader and writer (WIX_MANAGED_RES), using the real burn.exe stubs.
    /// </summary>
    public class PeResourcesFixture
    {
        private static readonly string[] Platforms = { "x86", "x64", "arm64" };

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void ManagedReaderFindsBurnResources()
        {
            foreach (var platform in Platforms)
            {
                var burn = BurnStub(platform);
                var native = IsWindows ? ReadAll(burn, managed: false) : null;
                var managed = ReadAll(burn, managed: true);

                Assert.NotEmpty(managed);
                Assert.True(managed.ContainsKey(Key(ResourceType.Version, "#1", 1033)), "version resource missing for " + platform);
                Assert.True(managed.ContainsKey(Key(ResourceType.Manifest, "#1", 1033)), "manifest resource missing for " + platform);

                var manifest = Encoding.UTF8.GetString(managed[Key(ResourceType.Manifest, "#1", 1033)]);
                Assert.Contains("WixToolset.Burn", manifest);

                if (native != null)
                {
                    WixAssert.CompareLineByLine(native.Keys.OrderBy(k => k).ToArray(), managed.Keys.OrderBy(k => k).ToArray());
                    foreach (var pair in native)
                    {
                        Assert.Equal(pair.Value, managed[pair.Key]);
                    }
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void ManagedWriterRoundTripsWithoutChanges()
        {
            using (var fs = new DisposableFileSystem())
            {
                var burn = BurnStub("x64");
                var copy = Path.Combine(fs.GetFolder(true), "burn.exe");
                File.Copy(burn, copy);

                using (Managed())
                {
                    new ResourceCollection().Save(copy);
                }

                var before = ReadAll(burn, managed: true);
                var after = ReadAll(copy, managed: true);
                WixAssert.CompareLineByLine(before.Keys.OrderBy(k => k).ToArray(), after.Keys.OrderBy(k => k).ToArray());
                foreach (var pair in before)
                {
                    Assert.Equal(pair.Value, after[pair.Key]);
                }

                AssertValidPe(burn, copy);
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void ManagedWriterAddsReplacesAndDeletesResources()
        {
            foreach (var platform in Platforms)
            {
                using (var fs = new DisposableFileSystem())
                {
                    var burn = BurnStub(platform);
                    var copy = Path.Combine(fs.GetFolder(true), "bundle.exe");
                    File.Copy(burn, copy);

                    var big = new byte[300 * 1024];
                    new Random(1234).NextBytes(big);

                    using (Managed())
                    {
                        var resources = new ResourceCollection
                        {
                            new Resource(ResourceType.RCData, "#1", 1033, new byte[] { 1, 2, 3, 4, 5 }),
                            new Resource(ResourceType.Bitmap, "#1", 1033, big),
                            new Resource("CUSTOMTYPE", "NamedResource", 1033, Encoding.UTF8.GetBytes("named")),
                            new Resource(ResourceType.Manifest, "#1", 1033, Encoding.UTF8.GetBytes("<manifest/>")),
                        };
                        resources.Save(copy);
                    }

                    var before = ReadAll(burn, managed: true);
                    var after = ReadAll(copy, managed: true);

                    Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, after[Key(ResourceType.RCData, "#1", 1033)]);
                    Assert.Equal(big, after[Key(ResourceType.Bitmap, "#1", 1033)]);
                    Assert.Equal("named", Encoding.UTF8.GetString(after[Key("CUSTOMTYPE", "NamedResource", 1033)]));
                    Assert.Equal("<manifest/>", Encoding.UTF8.GetString(after[Key(ResourceType.Manifest, "#1", 1033)]));

                    foreach (var pair in before.Where(p => p.Key != Key(ResourceType.Manifest, "#1", 1033) && p.Key != Key(ResourceType.RCData, "#1", 1033) && p.Key != Key(ResourceType.Bitmap, "#1", 1033)))
                    {
                        Assert.Equal(pair.Value, after[pair.Key]);
                    }

                    AssertValidPe(burn, copy);

                    // Delete what was added; an update without data removes the resource.
                    using (Managed())
                    {
                        var deletes = new ResourceCollection
                        {
                            new Resource(ResourceType.Bitmap, "#1", 1033, null),
                            new Resource("CUSTOMTYPE", "NamedResource", 1033, null),
                        };
                        deletes.Save(copy);
                    }

                    var afterDelete = ReadAll(copy, managed: true);
                    Assert.False(afterDelete.ContainsKey(Key(ResourceType.Bitmap, "#1", 1033)));
                    Assert.False(afterDelete.ContainsKey(Key("CUSTOMTYPE", "NamedResource", 1033)));
                    Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, afterDelete[Key(ResourceType.RCData, "#1", 1033)]);
                    AssertValidPe(burn, copy);
                }
            }
        }

        [Fact]
        [Trait("Platform", "CrossPlatform")]
        public void WindowsAcceptsManagedWriterOutput()
        {
            if (!IsWindows)
            {
                return;
            }

            foreach (var platform in Platforms)
            {
                using (var fs = new DisposableFileSystem())
                {
                    var burn = BurnStub(platform);
                    var managedCopy = Path.Combine(fs.GetFolder(true), "managed.exe");
                    var nativeCopy = Path.Combine(fs.GetFolder(true), "native.exe");
                    File.Copy(burn, managedCopy);
                    File.Copy(burn, nativeCopy);

                    var big = new byte[200 * 1024];
                    new Random(99).NextBytes(big);

                    ResourceCollection Updates() => new ResourceCollection
                    {
                        new Resource(ResourceType.RCData, "#1", 1033, new byte[] { 9, 8, 7 }),
                        new Resource(ResourceType.Bitmap, "#1", 1033, big),
                        new Resource("CUSTOMTYPE", "NamedResource", 1033, Encoding.UTF8.GetBytes("named")),
                    };

                    using (Managed())
                    {
                        Updates().Save(managedCopy);
                    }

                    using (Native())
                    {
                        Updates().Save(nativeCopy);
                    }

                    // Same resources whichever way they were written and read.
                    var nativeOfNative = ReadAll(nativeCopy, managed: false);
                    var nativeOfManaged = ReadAll(managedCopy, managed: false);
                    var managedOfManaged = ReadAll(managedCopy, managed: true);
                    WixAssert.CompareLineByLine(nativeOfNative.Keys.OrderBy(k => k).ToArray(), nativeOfManaged.Keys.OrderBy(k => k).ToArray());
                    WixAssert.CompareLineByLine(nativeOfNative.Keys.OrderBy(k => k).ToArray(), managedOfManaged.Keys.OrderBy(k => k).ToArray());
                    foreach (var pair in nativeOfNative)
                    {
                        Assert.Equal(pair.Value, nativeOfManaged[pair.Key]);
                        Assert.Equal(pair.Value, managedOfManaged[pair.Key]);
                    }

                    // The loader maps the managed output as a real image (not only as a data file).
                    // LOAD_LIBRARY_AS_IMAGE_RESOURCE uses the section layout and works for any architecture.
                    foreach (var file in new[] { nativeCopy, managedCopy })
                    {
                        var resourceModule = LoadLibraryEx(file, IntPtr.Zero, 0x20 /* LOAD_LIBRARY_AS_IMAGE_RESOURCE */);
                        Assert.True(resourceModule != IntPtr.Zero, "image-resource load failed for " + file + ", error " + Marshal.GetLastWin32Error());
                        FreeLibrary(resourceModule);
                    }

                    // A full image load, only possible when the stub matches this process.
                    if (platform == "x64" && RuntimeInformation.ProcessArchitecture == Architecture.X64)
                    {
                        foreach (var file in new[] { nativeCopy, managedCopy })
                        {
                            var module = LoadLibraryEx(file, IntPtr.Zero, 0x1 /* DONT_RESOLVE_DLL_REFERENCES */);
                            Assert.True(module != IntPtr.Zero, "image load failed for " + file + ", error " + Marshal.GetLastWin32Error());
                            FreeLibrary(module);
                        }
                    }

                    // UpdateResource leaves a zero checksum alone, and so does the managed writer.
                    Assert.Equal(HeaderChecksum(nativeCopy), HeaderChecksum(managedCopy));

                    // A stub that has a checksum gets a correct one back: it matches what Windows computes.
                    var summed = Path.Combine(fs.GetFolder(true), "summed.exe");
                    File.Copy(burn, summed);
                    Assert.Equal(0u, MapFileAndCheckSum(summed, out _, out var seed));
                    SetHeaderChecksum(summed, seed);
                    using (Managed())
                    {
                        Updates().Save(summed);
                    }

                    Assert.Equal(0u, MapFileAndCheckSum(summed, out var headerSum, out var computedSum));
                    Assert.NotEqual(0u, headerSum);
                    Assert.Equal(computedSum, headerSum);
                }
            }
        }

        private static uint HeaderChecksum(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return BitConverter.ToUInt32(bytes, BitConverter.ToInt32(bytes, 0x3C) + 24 + 64);
        }

        private static void SetHeaderChecksum(string path, uint checksum)
        {
            var bytes = File.ReadAllBytes(path);
            BitConverter.GetBytes(checksum).CopyTo(bytes, BitConverter.ToInt32(bytes, 0x3C) + 24 + 64);
            File.WriteAllBytes(path, bytes);
        }

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static string BurnStub(string platform)
        {
            var path = Path.Combine(AppContext.BaseDirectory, platform, "burn.exe");
            Assert.True(File.Exists(path), "burn stub not found: " + path);
            return path;
        }

        private static string Key(ResourceType type, string name, int language)
        {
            return String.Join("|", ((string)type).ToUpperInvariant(), name.ToUpperInvariant(), language);
        }

        private static string Key(string type, string name, int language)
        {
            return Key(new ResourceType(type), name, language);
        }

        private static Dictionary<string, byte[]> ReadAll(string path, bool managed)
        {
            using (managed ? Managed() : Native())
            {
                var resources = new ResourceCollection();
                resources.Find(path);
                resources.Load(path);
                return resources.ToDictionary(r => Key(r.ResourceType, r.Name, r.Locale), r => r.Data);
            }
        }

        private static IDisposable Managed() => new EnvironmentVariable("WIX_MANAGED_RES", "1");

        private static IDisposable Native() => new EnvironmentVariable("WIX_MANAGED_RES", "0");

        private static void AssertValidPe(string original, string rewritten)
        {
            using (var originalStream = File.OpenRead(original))
            using (var rewrittenStream = File.OpenRead(rewritten))
            using (var originalReader = new PEReader(originalStream))
            using (var rewrittenReader = new PEReader(rewrittenStream))
            {
                var before = originalReader.PEHeaders;
                var after = rewrittenReader.PEHeaders;

                Assert.Equal(before.SectionHeaders.Length, after.SectionHeaders.Length);
                Assert.Equal(before.PEHeader.SectionAlignment, after.PEHeader.SectionAlignment);

                // Sections keep their order, alignment and, other than the resource section, their content.
                var resourceRva = after.PEHeader.ResourceTableDirectory.RelativeVirtualAddress;
                var previousEnd = 0;
                for (var i = 0; i < after.SectionHeaders.Length; i++)
                {
                    var header = after.SectionHeaders[i];
                    Assert.Equal(before.SectionHeaders[i].Name, header.Name);
                    Assert.Equal(0, header.VirtualAddress % after.PEHeader.SectionAlignment);
                    Assert.Equal(0, header.PointerToRawData % after.PEHeader.FileAlignment);
                    Assert.True(header.VirtualAddress >= previousEnd, "sections overlap in memory at " + header.Name);
                    previousEnd = header.VirtualAddress + AlignUp(header.VirtualSize, after.PEHeader.SectionAlignment);

                    if (header.VirtualAddress != resourceRva)
                    {
                        var oldBytes = originalReader.GetSectionData(before.SectionHeaders[i].VirtualAddress).GetContent();
                        var newBytes = rewrittenReader.GetSectionData(header.VirtualAddress).GetContent();
                        Assert.Equal(oldBytes.Length, newBytes.Length);
                        Assert.True(oldBytes.SequenceEqual(newBytes), "section content changed: " + header.Name);
                    }
                }

                Assert.True(after.PEHeader.SizeOfImage >= previousEnd);
                Assert.Equal(AlignUp(previousEnd, after.PEHeader.SectionAlignment), after.PEHeader.SizeOfImage);

                // The relocation directory still points at the relocation section.
                var relocation = after.SectionHeaders.Single(s => s.Name == ".reloc");
                Assert.Equal(relocation.VirtualAddress, after.PEHeader.BaseRelocationTableDirectory.RelativeVirtualAddress);
                Assert.Equal(before.PEHeader.BaseRelocationTableDirectory.Size, after.PEHeader.BaseRelocationTableDirectory.Size);

                // The file is exactly as long as the last section says.
                var last = after.SectionHeaders.OrderBy(s => s.PointerToRawData).Last();
                Assert.Equal(last.PointerToRawData + last.SizeOfRawData, (int)new FileInfo(rewritten).Length);

                if (before.PEHeader.CheckSum != 0)
                {
                    Assert.NotEqual(0u, after.PEHeader.CheckSum);
                }
            }
        }

        private static int AlignUp(int value, int alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("imagehlp.dll", CharSet = CharSet.Unicode)]
        private static extern uint MapFileAndCheckSum(string fileName, out uint headerSum, out uint checkSum);

        private sealed class EnvironmentVariable : IDisposable
        {
            private readonly string name;
            private readonly string previous;

            public EnvironmentVariable(string name, string value)
            {
                this.name = name;
                this.previous = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(this.name, this.previous);
            }
        }
    }
}
