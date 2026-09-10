// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;

namespace Microsoft.CodeAnalysis.BuildTasks
{
    [MSBuildMultiThreadableTask]
    [MSBuildDeclaredIOTask]
    [MSBuildDeclaredIORequiresUnset(nameof(AdditionalLibPaths))]
    [MSBuildDeclaredIORequiresUnset(nameof(Deterministic))]
    [MSBuildDeclaredIORequiresUnset(nameof(EnvironmentVariables))]
    [MSBuildDeclaredIORequiresUnset(nameof(ErrorLog))]
    [MSBuildDeclaredIORequiresUnset(nameof(FailIfNotIncremental))]
    [MSBuildDeclaredIORequiresUnset(nameof(GeneratedFilesOutputPath))]
    [MSBuildDeclaredIORequiresUnset(nameof(KeyContainer))]
    [MSBuildDeclaredIORequiresUnset(nameof(NoConfig))]
    [MSBuildDeclaredIORequiresUnset(nameof(ProvideCommandLineArgs))]
    [MSBuildDeclaredIORequiresUnset(nameof(ResponseFiles))]
    [MSBuildDeclaredIORequiresUnset(nameof(SharedCompilationId))]
    [MSBuildDeclaredIORequiresUnset(nameof(SkipCompilerExecution))]
    [MSBuildDeclaredIORequiresUnset(nameof(Timeout))]
    [MSBuildDeclaredIORequiresUnset(nameof(ToolExe))]
    [MSBuildDeclaredIORequiresUnset(nameof(ToolPath))]
    [MSBuildDeclaredIORequiresUnset(nameof(UseCommandProcessor))]
    [MSBuildDeclaredIORequiresUnset(nameof(UseHostCompilerIfAvailable))]
    [MSBuildDeclaredIORequiresUnset(nameof(VsSessionGuid))]
    public sealed class DeclaredIOCsc : Csc
    {
        public DeclaredIOCsc()
        {
            Deterministic = true;
            NoConfig = true;
            UseHostCompilerIfAvailable = false;
        }

        public ITaskItem[]? DeclaredInputs { get; set; }

        public ITaskItem[]? DeclaredOutputs { get; set; }
    }
}
