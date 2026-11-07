// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Managed
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using WixToolset.Core.Native.Msi;
    using Lib = IsWiX.WindowsInstaller;

    /// <summary>
    /// Selects between msi.dll (Windows) and the managed IsWiX.WindowsInstaller engine.
    /// The managed engine is the default on non-Windows platforms. On Windows it can be
    /// forced by setting WIX_MANAGED_MSI=1 (used to run the conformance tests).
    /// </summary>
    internal static class ManagedBackend
    {
        internal const string EnvironmentVariableName = "WIX_MANAGED_MSI";

        private static readonly bool enabled = Detect();

        internal static bool Enabled => enabled;

        private static bool Detect()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName)?.Trim();

            if (!String.IsNullOrEmpty(value))
            {
                return value == "1" || String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }

            return !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        internal static MsiException Translate(Lib.InstallerException e, string caller = null)
        {
            return new MsiException(e.ErrorCode, "The managed MSI engine failed" + (caller == null ? "" : " (" + caller + ")") + ": " + e.GetType().Name + " (MSI error " + e.ErrorCode + "): " + e.Message + " AT " + (e.StackTrace ?? "(no stack)").Replace("\r", " ").Replace("\n", " |"));
        }

        internal static void Run(Action action, [CallerMemberName] string caller = "")
        {
            try
            {
                action();
            }
            catch (Lib.InstallerException e)
            {
                throw Translate(e, caller);
            }
            catch (NotImplementedException e)
            {
                throw NotImplemented(e, caller);
            }
            catch (ArgumentException e)
            {
                throw Wrap(87, e, caller);
            }
        }

        internal static T Run<T>(Func<T> func, [CallerMemberName] string caller = "")
        {
            try
            {
                return func();
            }
            catch (Lib.InstallerException e)
            {
                throw Translate(e, caller);
            }
            catch (NotImplementedException e)
            {
                throw NotImplemented(e, caller);
            }
            catch (ArgumentException e)
            {
                throw Wrap(87, e, caller);
            }
        }

        // The library raises NotImplementedException for features it lacks. Surface where, so gaps are easy to find.
        internal static MsiException NotImplemented(NotImplementedException e, string caller)
        {
            return Wrap(120, e, caller);
        }

        // Library errors that map to plain .NET exceptions lose their origin. Keep it in the message.
        internal static MsiException Wrap(int error, Exception e, string caller)
        {
            return new MsiException(error, "The managed MSI engine failed (" + caller + "): " + e.GetType().Name + ": " + e.Message + Environment.NewLine + e.StackTrace);
        }
    }
}
