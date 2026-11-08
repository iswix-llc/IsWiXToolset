// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.BaseBuildTasks
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using Microsoft.Build.Framework;
    using Microsoft.Build.Utilities;

    public abstract class BaseToolsetTask : ToolTask
    {
        /// <summary>
        /// Gets or sets additional options that are appended the the tool command-line.
        /// </summary>
        /// <remarks>
        /// This allows the task to support extended options in the tool which are not
        /// explicitly implemented as properties on the task.
        /// </remarks>
        public string AdditionalOptions { get; set; }

        /// <summary>
        /// Gets or sets whether to display the logo.
        /// </summary>
        public bool NoLogo { get; set; }

        /// <summary>
        /// Gets or sets whether all warnings should be suppressed.
        /// </summary>
        public bool SuppressAllWarnings { get; set; }

        /// <summary>
        /// Gets or sets a list of specific warnings to be suppressed.
        /// </summary>
        public string[] SuppressSpecificWarnings { get; set; }

        /// <summary>
        /// Gets or sets whether all warnings should be treated as errors.
        /// </summary>
        public bool TreatWarningsAsErrors { get; set; }

        /// <summary>
        /// Gets or sets a list of specific warnings to treat as errors.
        /// </summary>
        public string[] TreatSpecificWarningsAsErrors { get; set; }

        /// <summary>
        /// Gets or sets whether to display verbose output.
        /// </summary>
        public bool VerboseOutput { get; set; }

        /// <summary>
        /// Get the path to the executable.
        /// </summary>
        /// <remarks>
        /// ToolTask only calls GenerateFullPathToTool when the ToolPath property is not set.
        /// WiX never sets the ToolPath property, but the user can through $(WixToolDir).
        /// If we return only a file name, ToolTask will search the system paths for it.
        /// </remarks>
        protected sealed override string GenerateFullPathToTool()
        {
            var defaultToolFullPath = this.GetDefaultToolFullPath();

#if NETCOREAPP
            // If we're pointing at an executable use that.
            if (IsSelfExecutable(defaultToolFullPath, out var finalToolFullPath))
            {
                return finalToolFullPath;
            }

            // Otherwise, use "dotnet.exe" to run an assembly dll.
            return Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
#else
            return defaultToolFullPath;
#endif
        }

        /// <summary>
        /// Builds a command line from options in this and derivative tasks.
        /// </summary>
        /// <remarks>
        /// Derivative classes should call BuildCommandLine() on the base class to ensure that common command line options are added to the command.
        /// </remarks>
        protected virtual void BuildCommandLine(WixCommandLineBuilder commandLineBuilder)
        {
            commandLineBuilder.AppendIfTrue("-nologo", this.NoLogo);
            commandLineBuilder.AppendArrayIfNotNull("-sw", this.SuppressSpecificWarnings);
            commandLineBuilder.AppendIfTrue("-sw", this.SuppressAllWarnings);
            commandLineBuilder.AppendIfTrue("-v", this.VerboseOutput);
            commandLineBuilder.AppendArrayIfNotNull("-wx", this.TreatSpecificWarningsAsErrors);
            commandLineBuilder.AppendIfTrue("-wx", this.TreatWarningsAsErrors);
            commandLineBuilder.AppendTextIfNotNull(this.AdditionalOptions);
        }

        protected sealed override string GenerateResponseFileCommands()
        {
            var commandLineBuilder = new WixCommandLineBuilder();
            this.BuildCommandLine(commandLineBuilder);
            return commandLineBuilder.ToString();
        }

        // The most recent output lines the tool wrote, kept so a failure that reported no error MSBuild recognizes can still be explained.
        private const int MaximumCapturedOutputLines = 100;
        private readonly Queue<string> capturedToolOutput = new Queue<string>();

        protected override void LogEventsFromTextOutput(string singleLine, MessageImportance messageImportance)
        {
            if (!String.IsNullOrWhiteSpace(singleLine))
            {
                this.capturedToolOutput.Enqueue(singleLine);

                if (this.capturedToolOutput.Count > MaximumCapturedOutputLines)
                {
                    this.capturedToolOutput.Dequeue();
                }
            }

            base.LogEventsFromTextOutput(singleLine, messageImportance);
        }

        protected override bool HandleTaskExecutionErrors()
        {
            // When the tool fails without writing an error in a form MSBuild recognizes (for example an unhandled exception or a
            // host failure), MSBuild would report only "exited with code N". Show what the tool wrote so the cause is visible.
            if (!this.HasLoggedErrors && this.capturedToolOutput.Count > 0)
            {
                this.Log.LogError("{0} exited with code {1} without reporting an error. Its output was:{2}{3}", this.ToolName, this.ExitCode, Environment.NewLine, String.Join(Environment.NewLine, this.capturedToolOutput.ToArray()));
            }

            return base.HandleTaskExecutionErrors();
        }

#if NETCOREAPP
        protected override string GenerateCommandLineCommands()
        {
            // If the target tool path is an executable, we don't need to add anything to the command-line.
            var toolFullPath = this.GetToolFullPath();

            if (IsSelfExecutable(toolFullPath, out var finalToolFullPath))
            {
                return null;
            }
            else // we're using "dotnet.exe" to run the assembly so add "exec" plus path to the command-line.
            {
                return $"exec \"{finalToolFullPath}\"";
            }
        }

        private static bool IsSelfExecutable(string proposedToolFullPath, out string finalToolFullPath)
        {
            var toolFullPathWithoutExtension = Path.Combine(Path.GetDirectoryName(proposedToolFullPath), Path.GetFileNameWithoutExtension(proposedToolFullPath));
            var exeExtension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : String.Empty;
            var exeToolFullPath = $"{toolFullPathWithoutExtension}{exeExtension}";
            if (File.Exists(exeToolFullPath))
            {
                finalToolFullPath = exeToolFullPath;
                return true;
            }

            finalToolFullPath = $"{toolFullPathWithoutExtension}.dll";
            return false;
        }
#else
        private string FindArchitectureSpecificToolPath(string baseFolder)
        {
            var checkedPaths = new List<string>();

            // First try to find a folder that matches this task's architecture.
            var archFolder = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

            var path = Path.Combine(baseFolder, archFolder, this.ToolExe);

            if (File.Exists(path))
            {
                return path;
            }

            checkedPaths.Add(path);

            // Try to fallback to "x86" folder since it tends to run on all architectures.
            if (!String.Equals(archFolder, "x86", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(baseFolder, "x86", this.ToolExe);

                if (File.Exists(path))
                {
                    return path;
                }

                checkedPaths.Add(path);
            }

            // Return empty, even though this isn't likely to be there.
            path = Path.Combine(baseFolder, this.ToolExe);

            if (File.Exists(path))
            {
                return path;
            }

            checkedPaths.Add(path);

            this.Log.LogError("Cannot find tool executable {0} at any of the checked paths: {1}. This is unexpected and will cause later commands to fail.", this.ToolExe, String.Join(", ", checkedPaths));

            return path;
        }
#endif

        private string GetDefaultToolFullPath()
        {
#if NETCOREAPP
                var thisTaskFolder = Path.GetDirectoryName(Path.GetFullPath(typeof(BaseToolsetTask).Assembly.Location));

                return Path.Combine(thisTaskFolder, this.ToolExe);
#else
                var thisTaskFolder = Path.GetDirectoryName(Path.GetFullPath(new Uri(typeof(BaseToolsetTask).Assembly.CodeBase).LocalPath));

                return this.FindArchitectureSpecificToolPath(thisTaskFolder);
#endif
        }

        private string GetToolFullPath()
        {
            if (String.IsNullOrEmpty(this.ToolPath))
            {
                return this.GetDefaultToolFullPath();
            }

            return Path.Combine(this.ToolPath, this.ToolExe);
        }
    }
}
