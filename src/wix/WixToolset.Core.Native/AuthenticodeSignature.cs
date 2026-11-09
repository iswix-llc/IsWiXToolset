// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native
{
    using WixToolset.Core.Native.Managed;

    /// <summary>
    /// Reads the certificate that signed a file.
    /// </summary>
    public static class AuthenticodeSignature
    {
        /// <summary>
        /// Gets whether signatures are read by the managed implementation (always on non-Windows platforms,
        /// on Windows when WIX_MANAGED_MSI is set to 1) instead of the Windows crypto APIs.
        /// </summary>
        public static bool UseManagedReader => ManagedBackend.Enabled;

        /// <summary>
        /// Reads the certificate of the first signer of a PE file or a cabinet using the managed implementation.
        /// The signature itself is not verified.
        /// </summary>
        /// <param name="path">Path to the file.</param>
        /// <returns>The DER encoded certificate, or null when the file is not signed.</returns>
        public static byte[] ReadSignerCertificate(string path)
        {
            return ManagedAuthenticode.ReadSignerCertificate(path)?.RawData;
        }
    }
}
