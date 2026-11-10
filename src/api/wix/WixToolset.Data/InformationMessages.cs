// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Data
{
    using System;

    public static class InformationMessages
    {
        public static Message ValidationNotPerformed()
        {
            return Message(null, Ids.ValidationNotPerformed, "ICE validation was not performed: it requires the Windows Installer engine, which the managed backend does not provide. Run 'wix msi validate' on Windows, or set SuppressValidation=true to hide this warning.");
        }

        private static Message Message(SourceLineNumber sourceLineNumber, Ids id, string format, params object[] args)
        {
            return new Message(sourceLineNumber, MessageLevel.Information, (int)id, format, args);
        }

        public enum Ids
        {
            ValidationNotPerformed = 1164,
        }
    }
}
