// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolsetTest.CoreNative
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using WixToolset.Core.Native.Msi;
    using Xunit;

    // PeVersionReader is internal, so these tests reach it by reflection.
    [Trait("Platform", "CrossPlatform")]
    public class PeVersionReaderFixture
    {
        private static readonly Type Reader = typeof(Installer).Assembly.GetType("WixToolset.Core.Native.Managed.PeVersionReader", true);

        [Fact]
        public void ReportsEveryTranslationLanguageOnce()
        {
            // A Var block: wLength, wValueLength (bytes), wType, "Translation", padding, then langid/codepage pairs.
            var blob = new List<byte>();
            blob.AddRange(new byte[] { 0, 0, 16, 0, 0, 0 });
            blob.AddRange(Encoding.Unicode.GetBytes("Translation\0"));
            while (blob.Count % 4 != 0)
            {
                blob.Add(0);
            }

            foreach (var langid in new[] { 1033, 1031, 1033, 1036 })
            {
                blob.Add((byte)(langid & 0xff));
                blob.Add((byte)(langid >> 8));
                blob.Add(0xb0);
                blob.Add(0x04);
            }

            var method = Reader.GetMethod("FindTranslations", BindingFlags.NonPublic | BindingFlags.Static);
            var data = blob.ToArray();
            var result = (string)method.Invoke(null, new object[] { data, 0, data.Length });

            Assert.Equal("1033,1031,1036", result);
        }

        [Fact]
        public void AddsTheLanguagesOfSatelliteMuiFiles()
        {
            var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var culture in new[] { "fr-FR", "de-DE", "en-us", "zh-TW" })
                {
                    Directory.CreateDirectory(Path.Combine(folder, culture));
                    File.WriteAllText(Path.Combine(folder, culture, "thing.dll.mui"), "x");
                }

                Directory.CreateDirectory(Path.Combine(folder, "ja-JP")); // no .mui for this file
                File.WriteAllText(Path.Combine(folder, "thing.dll"), "x");

                var method = Reader.GetMethod("AddMuiLanguages", BindingFlags.NonPublic | BindingFlags.Static);
                var result = (string)method.Invoke(null, new object[] { Path.Combine(folder, "thing.dll"), "1033" });

                // Folders ascending (de-DE, en-us, fr-FR, zh-TW), each new language put in front, so the file's own language is last.
                Assert.Equal("1028,1036,1031,1033", result);

                var none = (string)method.Invoke(null, new object[] { Path.Combine(folder, "other.dll"), "1033" });
                Assert.Equal("1033", none);
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        [Fact]
        public void ReadsTheVersionOfATrueTypeFont()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ttf");
            try
            {
                File.WriteAllBytes(path, BuildFont("Version 1.10; ttfautohint (v1.8)"));

                var args = new object[] { path, null, null };
                var found = (bool)Reader.GetMethod("TryRead", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);

                Assert.True(found);
                Assert.Equal("1.10.0.0", args[1]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // A font with a single 'name' table holding one Windows English name id 5 record.
        private static byte[] BuildFont(string versionText)
        {
            var text = Encoding.BigEndianUnicode.GetBytes(versionText);
            var table = new List<byte>();
            AddBE16(table, 0);        // format
            AddBE16(table, 1);        // count
            AddBE16(table, 6 + 12);   // string offset
            AddBE16(table, 3);        // platform: Windows
            AddBE16(table, 1);        // encoding: Unicode BMP
            AddBE16(table, 0x409);    // language: en-US
            AddBE16(table, 5);        // name id: version
            AddBE16(table, text.Length);
            AddBE16(table, 0);
            table.AddRange(text);

            var font = new List<byte>();
            AddBE16(font, 1);
            AddBE16(font, 0);         // sfnt version 0x00010000
            AddBE16(font, 1);         // table count
            AddBE16(font, 0);
            AddBE16(font, 0);
            AddBE16(font, 0);
            font.AddRange(Encoding.ASCII.GetBytes("name"));
            AddBE16(font, 0);
            AddBE16(font, 0);         // checksum
            AddBE16(font, 0);
            AddBE16(font, 28);        // offset
            AddBE16(font, 0);
            AddBE16(font, table.Count); // length
            font.AddRange(table);
            return font.ToArray();
        }

        private static void AddBE16(List<byte> bytes, int value)
        {
            bytes.Add((byte)((value >> 8) & 0xff));
            bytes.Add((byte)(value & 0xff));
        }
    }
}
