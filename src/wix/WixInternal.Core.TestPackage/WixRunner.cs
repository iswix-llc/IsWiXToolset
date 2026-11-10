// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixInternal.Core.TestPackage
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using WixToolset.Core;
    using WixToolset.Core.Burn;
    using WixToolset.Core.ExtensionCache;
    using WixToolset.Core.WindowsInstaller;
    using WixToolset.Data;
    using WixToolset.Extensibility.Services;

    /// <summary>
    /// Utility class to emulate wix.exe with standard backends.
    /// </summary>
    public static class WixRunner
    {
        /// <summary>
        /// Emulates calling wix.exe with standard backends.
        /// </summary>
        /// <param name="args"></param>
        /// <param name="messages"></param>
        /// <param name="warningsAsErrors"></param>
        /// <param name="skipAcceptEula"></param>
        /// <returns></returns>
        public static int Execute(string[] args, out List<Message> messages, bool warningsAsErrors = true, bool skipAcceptEula = false)
        {
            var serviceProvider = WixToolsetServiceProviderFactory.CreateServiceProvider();
            var task = Execute(args, serviceProvider, out messages, warningsAsErrors: warningsAsErrors, skipAcceptEula);
            return task.Result;
        }

        /// <summary>
        /// Emulates calling wix.exe with standard backends.
        /// This overload always treats warnings as errors.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public static WixRunnerResult Execute(params string[] args)
        {
            return Execute(true, args);
        }

        /// <summary>
        /// Emulates calling wix.exe with standard backends.
        /// </summary>
        /// <param name="warningsAsErrors"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public static WixRunnerResult Execute(bool warningsAsErrors, params string[] args)
        {
            var serviceProvider = WixToolsetServiceProviderFactory.CreateServiceProvider();
            var exitCode = Execute(args, serviceProvider, out var messages, warningsAsErrors: warningsAsErrors);
            return new WixRunnerResult { ExitCode = exitCode.Result, Messages = messages.ToArray() };
        }

        /// <summary>
        /// Emulates calling wix.exe with standard backends.
        /// </summary>
        /// <param name="args"></param>
        /// <param name="coreProvider"></param>
        /// <param name="messages"></param>
        /// <param name="warningsAsErrors"></param>
        /// <param name="skipAcceptEula"></param>
        /// <returns></returns>
        public static Task<int> Execute(string[] args, IWixToolsetCoreServiceProvider coreProvider, out List<Message> messages, bool warningsAsErrors = true, bool skipAcceptEula = false)
        {
            coreProvider.AddWindowsInstallerBackend()
                        .AddBundleBackend()
                        .AddExtensionCacheManager();

            var listener = new TestMessageListener();

            messages = listener.Messages;

            var messaging = coreProvider.GetService<IMessaging>();
            messaging.SetListener(listener);

            var arguments = new List<string>(args);
            if (!skipAcceptEula && !arguments.Contains("-acceptEula"))
            {
                arguments.Add("-acceptEula");
                arguments.Add("wix" + SomeVerInfo.Major);
            }

            // The managed MSI backend cannot run ICE validation and says so with a warning, which warnings-as-errors would fail every test on.
            if (ManagedBackendIsOn() && !arguments.Contains("-sw1164"))
            {
                arguments.Add("-sw1164");
            }

            if (warningsAsErrors)
            {
                arguments.Add("-wx");
            }

            var commandLine = coreProvider.GetService<ICommandLine>();
            var command = commandLine.CreateCommand(arguments.ToArray());
            return command?.ExecuteAsync(CancellationToken.None) ?? Task.FromResult(1);
        }

        // The same rule WixToolset.Core.Native uses: WIX_MANAGED_MSI decides, otherwise the managed engine is the default off Windows.
        private static bool ManagedBackendIsOn()
        {
            var value = Environment.GetEnvironmentVariable("WIX_MANAGED_MSI")?.Trim();
            if (!String.IsNullOrEmpty(value))
            {
                return value == "1" || String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }

            return !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
    }
}
