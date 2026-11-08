// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolsetTest.BuildTasks
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Microsoft.Build.Framework;
    using WixToolset.BaseBuildTasks;
    using Xunit;

    public class BaseToolsetTaskFixture
    {
        [Fact]
        public void ToolFailureWithoutRecognizedErrorShowsToolOutput()
        {
            var engine = new RecordingBuildEngine();
            var task = new CmdTask("echo something unexpected happened & echo second line & exit /b 1") { BuildEngine = engine };

            Assert.False(task.Execute());

            var error = Assert.Single(engine.Errors);
            Assert.Contains("exited with code 1", error);
            Assert.Contains("something unexpected happened", error);
            Assert.Contains("second line", error);
        }

        [Fact]
        public void ToolFailureWithRecognizedErrorIsNotRepeated()
        {
            var engine = new RecordingBuildEngine();
            var task = new CmdTask("echo test.wxs(7) : error WIX0027: the problem & exit /b 1") { BuildEngine = engine };

            Assert.False(task.Execute());

            var error = Assert.Single(engine.Errors);
            Assert.Contains("the problem", error);
            Assert.DoesNotContain("without reporting an error", error);
        }

        [Fact]
        public void SuccessfulToolLogsNoError()
        {
            var engine = new RecordingBuildEngine();
            var task = new CmdTask("echo all good") { BuildEngine = engine };

            Assert.True(task.Execute());

            Assert.Empty(engine.Errors);
        }

        private class CmdTask : BaseToolsetTask
        {
            private readonly string arguments;

            public CmdTask(string arguments)
            {
                this.arguments = arguments;
                this.ToolPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
            }

            protected override string ToolName => "cmd.exe";

            protected override string GenerateCommandLineCommands() => "/c " + this.arguments;
        }

        private class RecordingBuildEngine : IBuildEngine
        {
            public List<string> Errors { get; } = new List<string>();

            public bool ContinueOnError => false;

            public int LineNumberOfTaskNode => 0;

            public int ColumnNumberOfTaskNode => 0;

            public string ProjectFileOfTaskNode => String.Empty;

            public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;

            public void LogCustomEvent(CustomBuildEventArgs e)
            {
            }

            public void LogErrorEvent(BuildErrorEventArgs e) => this.Errors.Add(e.Message);

            public void LogMessageEvent(BuildMessageEventArgs e)
            {
            }

            public void LogWarningEvent(BuildWarningEventArgs e)
            {
            }
        }
    }
}
