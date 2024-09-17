// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Workspaces;
using Xunit;

namespace Microsoft.CodeAnalysis.Workspace;

public class CscWorkspaceTests : IDisposable
{
    private readonly TempRoot _tempRoot = new TempRoot();

    public void Dispose()
    {
        _tempRoot.Dispose();
    }

    [Fact]
    public async Task TestOpenProject()
    {
        var dir = _tempRoot.CreateDirectory();
        dir.CreateFile("Program.cs").WriteAllText("""
System.Console.WriteLine(""Hello, World!"");
""");

        using (var workspace = CscWorkspace.Create())
        {
            var project = await workspace.OpenProjectFromCmdLineAsync($"Program.cs /out:a.exe", dir.Path);

            // Assert that there is a single project loaded.
            Assert.Single(workspace.CurrentSolution.ProjectIds);

            // Assert that the project does not have any diagnostics in Program.cs
            var document = project.Documents.First(d => d.Name == "Program.cs");
            var semanticModel = await document.GetSemanticModelAsync();
            var diagnostics = semanticModel!.GetDiagnostics();
            Assert.Empty(diagnostics);
        }
    }
}
