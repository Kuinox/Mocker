using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Mocker.API
{
    public sealed class MockerWeaver : ToolTask
    {
        /// <summary>
        /// Entry point of the program, your unit test dll for unit tests.
        /// </summary>
        public string EntryPointFile { get; set; }

        /// <summary>
        /// Copied DLLs that can be weaved
        /// </summary>
        public string[] CopiedDllAllowList { get; set; }

        /// <summary>
        /// Path to the DLL of the Mocker weaver.
        /// </summary>
        public string MockerWeaverPath { get; set; }

        protected override string ToolName => Path.GetFileName(GetDotNetPath());

        private int errorCount = 0;

        protected override string GenerateCommandLineCommands()
        {
            return $"exec \"{MockerWeaverPath}\"";
        }

        protected override string GenerateResponseFileCommands()
        {
            var sb = new StringBuilder();
            sb.Append(EntryPointFile);
            sb.Append(' ');

            foreach (var file in CopiedDllAllowList)
            {
                sb.Append(file);
                sb.Append(' ');
            }
            return sb.ToString();
        }

        protected override void LogEventsFromTextOutput(string singleLine, MessageImportance messageImportance)
        {
            errorCount++;
            base.LogEventsFromTextOutput(singleLine, messageImportance);
        }

        protected override bool HandleTaskExecutionErrors()
        {
            if (errorCount == 0)
            {
                var message = "Internal weaver error. Please open an issue with a repro case at https://github.com/Kuinox/Mocker/issues";
                Log.LogCriticalMessage(
                    subcategory: null, code: "MK0001", helpKeyword: null,
                    file: null,
                    lineNumber: 0, columnNumber: 0,
                    endLineNumber: 0, endColumnNumber: 0,
                    message: message.ToString());
            }

            return false;
        }

        private const string DotNetHostPathEnvironmentName = "DOTNET_HOST_PATH";

        // https://github.com/dotnet/roslyn/blob/020db28fa9b744146e6f072dbdc6bf3e62c901c1/src/Compilers/Shared/RuntimeHostInfo.cs#L59
        private static string GetDotNetPath()
        {
            if (Environment.GetEnvironmentVariable(DotNetHostPathEnvironmentName) is string pathToDotNet)
            {
                return pathToDotNet;
            }

            var (fileName, sep) = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? ("dotnet.exe", ';')
                : ("dotnet", ':');

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var item in path.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var filePath = Path.Combine(item, fileName);
                    if (File.Exists(filePath))
                    {
                        return filePath;
                    }
                }
                catch
                {
                    // If we can't read a directory for any reason just skip it
                }
            }

            return fileName;
        }

        protected override string GenerateFullPathToTool() => Path.GetFullPath(GetDotNetPath());
    }
}
