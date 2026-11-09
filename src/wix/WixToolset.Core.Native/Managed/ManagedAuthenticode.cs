// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Managed
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;

    /// <summary>
    /// The certificate that signed a file.
    /// </summary>
    internal sealed class SignerCertificate
    {
        internal SignerCertificate(byte[] rawData, byte[] subjectPublicKeyInfo)
        {
            this.RawData = rawData;
            this.SubjectPublicKeyInfo = subjectPublicKeyInfo;
        }

        /// <summary>DER encoded certificate.</summary>
        internal byte[] RawData { get; }

        /// <summary>DER encoded SubjectPublicKeyInfo of the certificate.</summary>
        internal byte[] SubjectPublicKeyInfo { get; }
    }

    /// <summary>
    /// Reads the Authenticode signature of a file without Windows APIs: finds the PKCS #7 signature
    /// in a PE file (certificate table) or a cabinet (reserved header area) and returns the
    /// certificate of the first signer. The signature is not verified, only read.
    /// </summary>
    internal static class ManagedAuthenticode
    {
        private const int PkcsSignedDataCertificateType = 2;
        private const int MaximumSignatureSize = 64 * 1024 * 1024;

        /// <summary>
        /// Reads the signer's certificate of the file.
        /// </summary>
        /// <param name="path">Path to the file.</param>
        /// <returns>The signer certificate, or null when the file is not signed (or not a kind of file that can be signed).</returns>
        internal static SignerCertificate ReadSignerCertificate(string path)
        {
            byte[] signature;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                signature = ReadSignature(stream);
            }

            return signature == null ? null : GetSignerCertificate(signature);
        }

        private static byte[] ReadSignature(Stream stream)
        {
            var start = ReadAt(stream, 0, 4);

            if (start == null)
            {
                return null;
            }

            if (start[0] == 'M' && start[1] == 'Z')
            {
                return ReadPeSignature(stream);
            }

            if (start[0] == 'M' && start[1] == 'S' && start[2] == 'C' && start[3] == 'F')
            {
                return ReadCabinetSignature(stream);
            }

            return null;
        }

        private static byte[] ReadPeSignature(Stream stream)
        {
            var dosHeader = ReadAt(stream, 0x3c, 4);
            if (dosHeader == null)
            {
                return null;
            }

            var peOffset = BitConverter.ToInt32(dosHeader, 0);
            var peHeader = ReadAt(stream, peOffset, 26);
            if (peHeader == null || peHeader[0] != 'P' || peHeader[1] != 'E' || peHeader[2] != 0 || peHeader[3] != 0)
            {
                return null;
            }

            var magic = BitConverter.ToUInt16(peHeader, 24);
            var dataDirectoryOffset = (long)peOffset + 24 + (magic == 0x10b ? 96 : 112) + (4 * 8); // IMAGE_DIRECTORY_ENTRY_SECURITY is entry 4.
            var securityDirectory = ReadAt(stream, dataDirectoryOffset, 8);
            if (securityDirectory == null)
            {
                return null;
            }

            var certificateOffset = BitConverter.ToUInt32(securityDirectory, 0);
            var certificateSize = BitConverter.ToUInt32(securityDirectory, 4);
            if (certificateOffset == 0 || certificateSize < 8)
            {
                return null;
            }

            // WIN_CERTIFICATE: DWORD length, WORD revision, WORD certificate type, then the certificate.
            var certificateHeader = ReadAt(stream, certificateOffset, 8);
            if (certificateHeader == null)
            {
                return null;
            }

            var length = BitConverter.ToUInt32(certificateHeader, 0);
            var type = BitConverter.ToUInt16(certificateHeader, 6);
            if (type != PkcsSignedDataCertificateType || length <= 8 || length > MaximumSignatureSize)
            {
                return null;
            }

            return ReadAt(stream, certificateOffset + 8, (int)length - 8);
        }

        private static byte[] ReadCabinetSignature(Stream stream)
        {
            // CFHEADER: flags at offset 30; when cfhdrRESERVE_PRESENT (0x4) is set, the reserved
            // area size is a WORD at offset 36 and the area itself starts at offset 40.
            var header = ReadAt(stream, 30, 12);
            if (header == null || (BitConverter.ToUInt16(header, 0) & 0x4) == 0)
            {
                return null;
            }

            var reservedSize = BitConverter.ToUInt16(header, 6);
            if (reservedSize < 12)
            {
                return null;
            }

            // Signed cabinets store 0x00100000, the offset of the signature and its size in the reserved area.
            var reserved = ReadAt(stream, 40, 12);
            if (reserved == null || reserved[0] != 0 || reserved[1] != 0 || reserved[2] != 0x10 || reserved[3] != 0)
            {
                return null;
            }

            var offset = BitConverter.ToUInt32(reserved, 4);
            var size = BitConverter.ToUInt32(reserved, 8);
            if (offset == 0 || size == 0 || size > MaximumSignatureSize)
            {
                return null;
            }

            return ReadAt(stream, offset, (int)size);
        }

        private static byte[] ReadAt(Stream stream, long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > stream.Length)
            {
                return null;
            }

            stream.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    return null;
                }

                read += n;
            }

            return buffer;
        }

        // ContentInfo { OID, [0] { SignedData { version, digestAlgorithms, encapContentInfo, [0] certificates, [1] crls, signerInfos } } }
        private static SignerCertificate GetSignerCertificate(byte[] signature)
        {
            var contentInfo = Der.Read(signature, 0);
            var contentInfoParts = Der.Children(signature, contentInfo);
            if (contentInfoParts.Count < 2)
            {
                throw new CryptographicException("The signature is not a PKCS #7 ContentInfo.");
            }

            var signedDataParts = Der.Children(signature, contentInfoParts[1]);
            if (signedDataParts.Count < 1)
            {
                throw new CryptographicException("The signature does not contain SignedData.");
            }

            List<Der> certificates = null;
            Der signerInfos = null;

            var signedData = Der.Children(signature, signedDataParts[0]);
            for (var i = 3; i < signedData.Count; ++i)
            {
                if (signedData[i].Tag == 0xA0)
                {
                    certificates = Der.Children(signature, signedData[i]);
                }
                else if (signedData[i].Tag == 0x31)
                {
                    signerInfos = signedData[i];
                }
            }

            if (certificates == null || signerInfos == null)
            {
                throw new CryptographicException("The signature does not contain certificates or signers.");
            }

            var signers = Der.Children(signature, signerInfos);
            if (signers.Count < 1)
            {
                throw new CryptographicException("The signature does not contain a signer.");
            }

            // SignerInfo { version, issuerAndSerialNumber { issuer, serialNumber }, ... }
            var signerFields = Der.Children(signature, signers[0]);
            if (signerFields.Count < 2 || signerFields[1].Tag != 0x30)
            {
                throw new CryptographicException("The signer is not identified by issuer and serial number.");
            }

            var identifier = Der.Children(signature, signerFields[1]);
            if (identifier.Count < 2)
            {
                throw new CryptographicException("The signer identifier is incomplete.");
            }

            foreach (var certificate in certificates)
            {
                // Certificate { tbsCertificate { [0] version, serialNumber, signature, issuer, validity, subject, subjectPublicKeyInfo, ... }, ... }
                var certificateParts = Der.Children(signature, certificate);
                if (certificateParts.Count < 1)
                {
                    continue;
                }

                var fields = Der.Children(signature, certificateParts[0]);
                var first = fields.Count > 0 && fields[0].Tag == 0xA0 ? 1 : 0;
                if (fields.Count < first + 6)
                {
                    continue;
                }

                var serialNumber = fields[first];
                var issuer = fields[first + 2];

                if (Der.SameContent(signature, serialNumber, identifier[1]) && Der.SameElement(signature, issuer, identifier[0]))
                {
                    var subjectPublicKeyInfo = fields[first + 5];
                    return new SignerCertificate(Der.Element(signature, certificate), Der.Element(signature, subjectPublicKeyInfo));
                }
            }

            throw new CryptographicException("The certificate of the signer was not found in the signature.");
        }

        /// <summary>A DER encoded element: where it starts and where its content is.</summary>
        private sealed class Der
        {
            private Der(int tag, int start, int contentStart, int contentLength)
            {
                this.Tag = tag;
                this.Start = start;
                this.ContentStart = contentStart;
                this.ContentLength = contentLength;
            }

            internal int Tag { get; }

            internal int Start { get; }

            internal int ContentStart { get; }

            internal int ContentLength { get; }

            internal static Der Read(byte[] data, int offset)
            {
                if (offset < 0 || offset + 2 > data.Length)
                {
                    throw new CryptographicException("Unexpected end of DER data.");
                }

                var tag = data[offset];
                long length = data[offset + 1];
                var headerLength = 2;

                if ((length & 0x80) != 0)
                {
                    var lengthBytes = (int)(length & 0x7f);
                    if (lengthBytes == 0 || lengthBytes > 4 || offset + 2 + lengthBytes > data.Length)
                    {
                        throw new CryptographicException("Unsupported DER length.");
                    }

                    length = 0;
                    for (var i = 0; i < lengthBytes; ++i)
                    {
                        length = (length << 8) | data[offset + 2 + i];
                    }

                    headerLength = 2 + lengthBytes;
                }

                if (offset + headerLength + length > data.Length)
                {
                    throw new CryptographicException("DER element extends beyond the data.");
                }

                return new Der(tag, offset, offset + headerLength, (int)length);
            }

            internal static List<Der> Children(byte[] data, Der parent)
            {
                var result = new List<Der>();
                var offset = parent.ContentStart;
                var end = parent.ContentStart + parent.ContentLength;

                while (offset < end)
                {
                    var child = Read(data, offset);
                    result.Add(child);
                    offset = child.ContentStart + child.ContentLength;
                }

                return result;
            }

            internal static byte[] Element(byte[] data, Der element)
            {
                var result = new byte[element.ContentStart + element.ContentLength - element.Start];
                Buffer.BlockCopy(data, element.Start, result, 0, result.Length);
                return result;
            }

            internal static bool SameContent(byte[] data, Der a, Der b)
            {
                return Same(data, a.ContentStart, a.ContentLength, b.ContentStart, b.ContentLength);
            }

            internal static bool SameElement(byte[] data, Der a, Der b)
            {
                return Same(data, a.Start, a.ContentStart + a.ContentLength - a.Start, b.Start, b.ContentStart + b.ContentLength - b.Start);
            }

            private static bool Same(byte[] data, int offsetA, int lengthA, int offsetB, int lengthB)
            {
                if (lengthA != lengthB)
                {
                    return false;
                }

                for (var i = 0; i < lengthA; ++i)
                {
                    if (data[offsetA + i] != data[offsetB + i])
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
