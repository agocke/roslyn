using Microsoft.CodeAnalysis.Host.Mef;

namespace Microsoft.CodeAnalysis.Workspaces;

public sealed class CscWorkspace : Workspace
{
    private CscWorkspace()
        : base(MefHostServices.DefaultHost, nameof(CscWorkspace))
    { }

    public static CscWorkspace Create()
    {
        return new CscWorkspace();
    }

    public Task<Project> OpenProjectFromCmdLineAsync(string cmdLine, string baseDirectory)
    {
        var projInfo = CommandLineProject.CreateProjectInfo("TestProject", LanguageNames.CSharp, cmdLine, baseDirectory, this);

        OnProjectAdded(projInfo);
        UpdateReferencesAfterAdd();

        return Task.FromResult(this.CurrentSolution.GetProject(projInfo.Id)!);
    }
}
