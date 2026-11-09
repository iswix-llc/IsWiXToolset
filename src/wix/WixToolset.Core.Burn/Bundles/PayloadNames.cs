// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Burn.Bundles
{
    using System;

    internal static class PayloadNames
    {
        /// <summary>
        /// Builds the name of a payload that sits next to a package payload. Payload names always use Windows separators
        /// so the same bundle is built on every platform; <c>Path.GetDirectoryName</c> and <c>Path.Combine</c> would not
        /// split or join a name such as <c>FirstX86\FirstX86.msi</c> correctly on Linux and macOS.
        /// </summary>
        public static string Combine(string packagePayloadName, string relativeName)
        {
            var name = (packagePayloadName ?? String.Empty).Replace('/', '\\');
            var lastSeparator = name.LastIndexOf('\\');
            var directory = lastSeparator < 0 ? String.Empty : name.Substring(0, lastSeparator);
            var relative = relativeName.Replace('/', '\\');

            if (directory.Length == 0 || relative.StartsWith("\\", StringComparison.Ordinal))
            {
                return relative;
            }

            return directory + "\\" + relative;
        }
    }
}
