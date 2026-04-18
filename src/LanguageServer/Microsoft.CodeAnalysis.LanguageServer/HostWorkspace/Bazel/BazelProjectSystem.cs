// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.ProjectSystem;
using Microsoft.CodeAnalysis.Workspaces.ProjectSystem;
using Microsoft.Extensions.Logging;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.Bazel;

/// <summary>
/// A project system that loads C# projects from Bazel workspaces by running
/// <c>bazel aquery</c> to extract CSharpCompile actions and converting them
/// into Roslyn workspace projects.
/// </summary>
/// <remarks>
/// This is intentionally separate from <see cref="LanguageServerProjectLoader"/>
/// because Bazel has fundamentally different assumptions than MSBuild:
/// no .sln/.csproj files, no NuGet restore, no design-time builds.
/// Supports two loading strategies:
/// - eager: loads all targets on workspace open (good for small repos)
/// - onDemand: loads packages as files are opened (good for large repos)
/// </remarks>
[Export(typeof(BazelProjectSystem)), Shared]
internal sealed class BazelProjectSystem
{
    private readonly LanguageServerWorkspaceFactory _workspaceFactory;
    private readonly IFileChangeWatcher _fileChangeWatcher;
    private readonly ILogger _logger;

    /// <summary>
    /// Tracks loaded projects keyed by Bazel target label.
    /// </summary>
    private readonly Dictionary<string, LoadedProject> _loadedProjects = [];
    private readonly SemaphoreSlim _gate = new(initialCount: 1);

    /// <summary>
    /// Tracks in-flight package loads to de-duplicate concurrent requests.
    /// </summary>
    private readonly Dictionary<string, Task> _inFlightPackageLoads = [];

    /// <summary>
    /// Tracks which packages have been fully loaded to avoid re-querying.
    /// </summary>
    private readonly HashSet<string> _loadedPackages = new(StringComparer.Ordinal);

    // Stored workspace configuration from OpenWorkspace
    private string? _workspaceRoot;
    private string _bazelPath = "bazel";
    private string? _config;
    private ImmutableArray<string> _startupFlags = [];

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public BazelProjectSystem(
        LanguageServerWorkspaceFactory workspaceFactory,
        IFileChangeWatcher fileChangeWatcher,
        ILoggerFactory loggerFactory)
    {
        _workspaceFactory = workspaceFactory;
        _fileChangeWatcher = fileChangeWatcher;
        _logger = loggerFactory.CreateLogger<BazelProjectSystem>();
    }

    /// <summary>
    /// Opens a Bazel workspace. In eager mode, runs aquery and loads all targets.
    /// In onDemand mode, stores the config and waits for bazel/loadPackage notifications.
    /// </summary>
    public async Task OpenWorkspaceAsync(
        string workspaceRoot,
        string bazelPath,
        string? config,
        ImmutableArray<string> targets,
        ImmutableArray<string> startupFlags,
        bool isOnDemand,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Opening Bazel workspace at {WorkspaceRoot} (strategy: {Strategy})",
            workspaceRoot, isOnDemand ? "onDemand" : "eager");

        // Store workspace configuration for on-demand loading
        _workspaceRoot = workspaceRoot;
        _bazelPath = bazelPath;
        _config = config;
        _startupFlags = startupFlags;

        if (isOnDemand)
        {
            // In onDemand mode, signal that workspace is initialized immediately.
            // Individual packages will be loaded as files are opened.
            _logger.LogInformation("Bazel workspace configured for on-demand loading. Waiting for bazel/loadPackage notifications.");
            await ProjectInitializationHandler.SendProjectInitializationCompleteNotificationAsync();
            return;
        }

        // Eager mode: load all targets up front
        try
        {
            var aqueryJson = await RunBazelAqueryAsync(
                workspaceRoot, bazelPath, config, targets, startupFlags, cancellationToken);

            if (aqueryJson is null)
            {
                _logger.LogError("Failed to run bazel aquery. Ensure bazel is installed and the workspace has been built.");
                return;
            }

            var bazelProjects = BazelAqueryParser.ParseCSharpCompileActions(aqueryJson, workspaceRoot);
            _logger.LogInformation("Found {Count} CSharpCompile targets in Bazel workspace", bazelProjects.Length);

            await LoadBazelProjectsAsync(bazelProjects, workspaceRoot, cancellationToken);
        }
        finally
        {
            await ProjectInitializationHandler.SendProjectInitializationCompleteNotificationAsync();
            _logger.LogInformation("Bazel workspace initialization complete");
        }
    }

    /// <summary>
    /// Loads targets for a specific Bazel package on demand. Called by
    /// <see cref="LoadBazelPackageHandler"/> when a file is opened.
    /// De-duplicates concurrent requests for the same package.
    /// </summary>
    public Task LoadTargetsForPackageAsync(string packagePath, CancellationToken cancellationToken)
    {
        if (_workspaceRoot is null)
        {
            _logger.LogWarning("Cannot load package {Package}: no Bazel workspace is configured", packagePath);
            return Task.CompletedTask;
        }

        // Skip if already loaded
        if (_loadedPackages.Contains(packagePath))
        {
            _logger.LogDebug("Package {Package} already loaded, skipping", packagePath);
            return Task.CompletedTask;
        }

        Task loadTask;
        lock (_inFlightPackageLoads)
        {
            // Return existing in-flight task if one exists
            if (_inFlightPackageLoads.TryGetValue(packagePath, out var existing))
            {
                _logger.LogDebug("Package {Package} load already in flight, awaiting existing task", packagePath);
                return existing;
            }

            loadTask = LoadPackageCoreAsync(packagePath, cancellationToken);
            _inFlightPackageLoads[packagePath] = loadTask;
        }

        return loadTask;
    }

    private async Task LoadPackageCoreAsync(string packagePath, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Loading Bazel package: {Package}", packagePath);

            // Scope aquery to just this package's targets
            var targetPattern = $"{packagePath}:all";
            var targets = ImmutableArray.Create(targetPattern);

            var aqueryJson = await RunBazelAqueryAsync(
                _workspaceRoot!, _bazelPath, _config, targets, _startupFlags, cancellationToken);

            if (aqueryJson is null)
            {
                _logger.LogError("Failed to run bazel aquery for package {Package}", packagePath);
                return;
            }

            var bazelProjects = BazelAqueryParser.ParseCSharpCompileActions(aqueryJson, _workspaceRoot!);
            _logger.LogInformation("Found {Count} CSharpCompile targets in package {Package}",
                bazelProjects.Length, packagePath);

            if (bazelProjects.Length > 0)
            {
                await LoadBazelProjectsAsync(bazelProjects, _workspaceRoot!, cancellationToken);
            }

            _loadedPackages.Add(packagePath);
        }
        finally
        {
            lock (_inFlightPackageLoads)
            {
                _inFlightPackageLoads.Remove(packagePath);
            }
        }
    }

    /// <summary>
    /// Loads a batch of Bazel projects into the Roslyn workspace, removing any
    /// previously-loaded targets that are no longer present.
    /// </summary>
    private async Task LoadBazelProjectsAsync(
        ImmutableArray<BazelAqueryParser.BazelProject> bazelProjects,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var projectFactory = _workspaceFactory.HostProjectFactory;

        using (await _gate.DisposableWaitAsync(cancellationToken))
        {
            foreach (var bazelProject in bazelProjects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await LoadOrUpdateBazelProjectAsync(bazelProject, workspaceRoot, projectFactory, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load Bazel target {Target}", bazelProject.TargetLabel);
                }
            }
        }
    }

    private async Task LoadOrUpdateBazelProjectAsync(
        BazelAqueryParser.BazelProject bazelProject,
        string workspaceRoot,
        ProjectSystemProjectFactory projectFactory,
        CancellationToken cancellationToken)
    {
        var projectDisplayName = bazelProject.TargetLabel;
        if (string.IsNullOrEmpty(projectDisplayName))
            projectDisplayName = bazelProject.AssemblyName;

        _logger.LogInformation("Loading Bazel target: {Target} ({AssemblyName})",
            projectDisplayName, bazelProject.AssemblyName);

        var documents = bazelProject.SourceFiles
            .Select(sourcePath =>
            {
                var folders = GetFolders(sourcePath, workspaceRoot);
                return new DocumentFileInfo(sourcePath, sourcePath, isLinked: false, isGenerated: false, folders);
            })
            .ToImmutableArray();

        var additionalDocuments = bazelProject.AdditionalFiles
            .Select(filePath =>
            {
                var folders = GetFolders(filePath, workspaceRoot);
                return new DocumentFileInfo(filePath, filePath, isLinked: false, isGenerated: false, folders);
            })
            .ToImmutableArray();

        var analyzerConfigDocuments = bazelProject.AnalyzerConfigFiles
            .Select(filePath =>
            {
                var folders = GetFolders(filePath, workspaceRoot);
                return new DocumentFileInfo(filePath, filePath, isLinked: false, isGenerated: false, folders);
            })
            .ToImmutableArray();

        // Use a synthetic absolute path for FilePath based on workspace root + target label.
        // This avoids issues with Roslyn code that runs path APIs on ProjectFileInfo.FilePath.
        var syntheticProjectPath = Path.Combine(workspaceRoot, ".bazel-projects",
            bazelProject.TargetLabel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar).Replace(':', Path.DirectorySeparatorChar) + ".bzlproj");

        var projectFileInfo = new ProjectFileInfo
        {
            IsEmpty = false,
            Language = LanguageNames.CSharp,
            FilePath = syntheticProjectPath,
            OutputFilePath = bazelProject.OutputPath,
            OutputRefFilePath = null,
            IntermediateOutputFilePath = bazelProject.OutputPath,
            GeneratedFilesOutputDirectory = null,
            DefaultNamespace = null,
            TargetFramework = null,
            TargetFrameworkIdentifier = null,
            CommandLineArgs = bazelProject.CommandLineArgs,
            Documents = documents,
            AdditionalDocuments = additionalDocuments,
            AnalyzerConfigDocuments = analyzerConfigDocuments,
            ProjectReferences = [],
            ProjectCapabilities = [],
            ContentFilePaths = [],
            ProjectAssetsFilePath = null,
            PackageReferences = [],
            MetadataReferences = [],
            CodePage = 0,
            ChecksumAlgorithm = null,
            FileGlobs = [],
        };

        // If we already have a LoadedProject for this target, update it in place
        if (_loadedProjects.TryGetValue(bazelProject.TargetLabel, out var existingProject))
        {
            await existingProject.UpdateWithNewProjectInfoAsync(
                projectFileInfo,
                isMiscellaneousFile: false,
                hasAllInformation: true,
                _logger);
            return;
        }

        // Create a new project
        var projectCreationInfo = new ProjectSystemProjectCreationInfo
        {
            AssemblyName = bazelProject.AssemblyName,
            FilePath = null, // Bazel targets don't have a single project file on disk
            CompilationOutputAssemblyFilePath = bazelProject.OutputPath,
        };

        var projectSystemProject = await projectFactory.CreateAndAddToWorkspaceAsync(
            projectDisplayName,
            LanguageNames.CSharp,
            projectCreationInfo,
            _workspaceFactory.ProjectSystemHostInfo,
            cancellationToken).ConfigureAwait(false);

        var loadedProject = new LoadedProject(
            projectSystemProject,
            projectFactory,
            _fileChangeWatcher,
            _workspaceFactory.TargetFrameworkManager);

        await loadedProject.UpdateWithNewProjectInfoAsync(
            projectFileInfo,
            isMiscellaneousFile: false,
            hasAllInformation: true,
            _logger);

        _loadedProjects[bazelProject.TargetLabel] = loadedProject;
    }

    /// <summary>
    /// Computes folder path segments relative to the workspace root for display.
    /// </summary>
    private static ImmutableArray<string> GetFolders(string filePath, string workspaceRoot)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            return [];

        // Try to make relative to workspace root for cleaner folder display
        if (directory.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = directory[workspaceRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.Length > 0)
            {
                return [.. relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Where(s => !string.IsNullOrEmpty(s))];
            }
        }

        return [.. directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => !string.IsNullOrEmpty(s))];
    }

    private static async Task<string?> RunBazelAqueryAsync(
        string workspaceRoot,
        string bazelPath,
        string? config,
        ImmutableArray<string> targets,
        ImmutableArray<string> startupFlags,
        CancellationToken cancellationToken)
    {
        var targetPattern = targets.IsDefaultOrEmpty ? "//..." : string.Join(" + ", targets);
        var mnemonicFilter = $"mnemonic(\"CSharpCompile\", {targetPattern})";

        var arguments = new List<string>();
        foreach (var flag in startupFlags)
            arguments.Add(flag);

        arguments.Add("aquery");
        arguments.Add("--output=jsonproto");

        if (!string.IsNullOrEmpty(config))
        {
            arguments.Add($"--config={config}");
        }

        arguments.Add(mnemonicFilter);

        var psi = new ProcessStartInfo
        {
            FileName = bazelPath,
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(stdoutTask, stderrTask);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            return null;
        }

        return stdoutTask.Result;
    }
}
