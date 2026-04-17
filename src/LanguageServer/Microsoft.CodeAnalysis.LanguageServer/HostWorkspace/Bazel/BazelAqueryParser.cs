// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.Bazel;

/// <summary>
/// Parses Bazel aquery JSON proto output to extract CSharpCompile actions
/// and convert them into <see cref="ProjectFileInfo"/> objects suitable for
/// loading into the Roslyn workspace.
/// </summary>
internal static class BazelAqueryParser
{
    /// <summary>
    /// Represents a parsed Bazel CSharpCompile action with all the information
    /// needed to create a Roslyn workspace project.
    /// </summary>
    internal sealed record BazelProject
    {
        public required string TargetLabel { get; init; }
        public required string AssemblyName { get; init; }
        public required string? OutputPath { get; init; }
        public required ImmutableArray<string> SourceFiles { get; init; }
        public required ImmutableArray<string> AdditionalFiles { get; init; }
        public required ImmutableArray<string> AnalyzerConfigFiles { get; init; }
        public required ImmutableArray<string> CommandLineArgs { get; init; }
    }

    /// <summary>
    /// Parses the aquery JSON proto output and returns one <see cref="BazelProject"/>
    /// per CSharpCompile action found.
    /// </summary>
    /// <param name="aqueryJson">Raw JSON string from <c>bazel aquery --output=jsonproto</c>.</param>
    /// <param name="workspaceRoot">
    /// Absolute path to the Bazel workspace root. Used to resolve execroot-relative paths
    /// to absolute paths.
    /// </param>
    public static ImmutableArray<BazelProject> ParseCSharpCompileActions(string aqueryJson, string workspaceRoot)
    {
        using var doc = JsonDocument.Parse(aqueryJson);
        var root = doc.RootElement;

        var targets = ParseTargets(root);
        var execConfigs = DetectExecConfigurations(root);

        var results = ImmutableArray.CreateBuilder<BazelProject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!root.TryGetProperty("actions", out var actions))
            return results.ToImmutable();

        foreach (var action in actions.EnumerateArray())
        {
            var mnemonic = action.GetProperty("mnemonic").GetString();
            if (mnemonic is not "CSharpCompile")
                continue;

            // Skip exec/tool configuration actions (built for the host, not the target)
            var configId = action.GetProperty("configurationId").ToString();
            if (execConfigs.Contains(configId))
                continue;

            var targetId = action.GetProperty("targetId").ToString();
            var targetLabel = targets.GetValueOrDefault(targetId, "");
            var args = ParseArguments(action);
            var project = ParseCSharpAction(args, targetLabel, workspaceRoot);

            if (project is not null && seen.Add(project.TargetLabel))
                results.Add(project);
        }

        return results.ToImmutable();
    }

    private static BazelProject? ParseCSharpAction(List<string> args, string targetLabel, string workspaceRoot)
    {
        var sourceFiles = ImmutableArray.CreateBuilder<string>();
        var additionalFiles = ImmutableArray.CreateBuilder<string>();
        var analyzerConfigFiles = ImmutableArray.CreateBuilder<string>();
        var commandLineArgs = ImmutableArray.CreateBuilder<string>();
        string? outputPath = null;
        string? assemblyName = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("/out:") || arg.StartsWith("-out:"))
            {
                var outArg = arg[(arg.IndexOf(':') + 1)..];
                outputPath = ResolveToAbsolute(outArg, workspaceRoot);
                assemblyName = Path.GetFileNameWithoutExtension(outArg);
                commandLineArgs.Add("/out:" + outputPath);
            }
            else if (arg.StartsWith("/r:") || arg.StartsWith("-r:") ||
                     arg.StartsWith("/reference:") || arg.StartsWith("-reference:"))
            {
                var refPath = arg[(arg.IndexOf(':') + 1)..];
                var absoluteRef = ResolveToAbsolute(refPath, workspaceRoot);
                commandLineArgs.Add("/r:" + absoluteRef);
            }
            else if (arg.StartsWith("/analyzer:") || arg.StartsWith("-analyzer:"))
            {
                var analyzerPath = arg[(arg.IndexOf(':') + 1)..];
                var absoluteAnalyzer = ResolveToAbsolute(analyzerPath, workspaceRoot);
                commandLineArgs.Add("/analyzer:" + absoluteAnalyzer);
            }
            else if (arg.StartsWith("/additionalfile:") || arg.StartsWith("-additionalfile:"))
            {
                var additionalPath = arg[(arg.IndexOf(':') + 1)..];
                var absoluteAdditional = ResolveToAbsolute(additionalPath, workspaceRoot);
                additionalFiles.Add(absoluteAdditional);
                commandLineArgs.Add("/additionalfile:" + absoluteAdditional);
            }
            else if (arg.StartsWith("/analyzerconfig:") || arg.StartsWith("-analyzerconfig:"))
            {
                var configPath = arg[(arg.IndexOf(':') + 1)..];
                var absoluteConfig = ResolveToAbsolute(configPath, workspaceRoot);
                analyzerConfigFiles.Add(absoluteConfig);
                commandLineArgs.Add("/analyzerconfig:" + absoluteConfig);
            }
            else if (arg.StartsWith("/ruleset:") || arg.StartsWith("-ruleset:"))
            {
                var rulesetPath = arg[(arg.IndexOf(':') + 1)..];
                var absoluteRuleset = ResolveToAbsolute(rulesetPath, workspaceRoot);
                commandLineArgs.Add("/ruleset:" + absoluteRuleset);
            }
            else if (arg.StartsWith('/') || arg.StartsWith('-'))
            {
                // Other compiler flags — pass through as-is
                commandLineArgs.Add(arg);
            }
            else if (arg.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                     arg.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
            {
                // Source file
                var absoluteSource = ResolveToAbsolute(arg, workspaceRoot);
                sourceFiles.Add(absoluteSource);
            }
            // Skip the compiler executable itself (first arg is typically the csc path)
        }

        if (assemblyName is null)
            return null;

        return new BazelProject
        {
            TargetLabel = targetLabel,
            AssemblyName = assemblyName,
            OutputPath = outputPath,
            SourceFiles = sourceFiles.ToImmutable(),
            AdditionalFiles = additionalFiles.ToImmutable(),
            AnalyzerConfigFiles = analyzerConfigFiles.ToImmutable(),
            CommandLineArgs = commandLineArgs.ToImmutable(),
        };
    }

    /// <summary>
    /// Resolves a potentially execroot-relative path to an absolute path.
    /// </summary>
    private static string ResolveToAbsolute(string path, string workspaceRoot)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        // Bazel paths are relative to the execroot, which is typically under
        // <output_base>/execroot/<workspace_name>. The execroot symlink
        // bazel-<workspace_name> in the workspace root points there.
        // For simplicity, resolve relative to the workspace root, which is
        // where the bazel-bin, bazel-out symlinks point from.
        return Path.GetFullPath(Path.Combine(workspaceRoot, path));
    }

    private static Dictionary<string, string> ParseTargets(JsonElement root)
    {
        var targets = new Dictionary<string, string>();
        if (root.TryGetProperty("targets", out var targetsArray))
        {
            foreach (var t in targetsArray.EnumerateArray())
            {
                var id = t.GetProperty("id").ToString();
                var label = t.GetProperty("label").GetString() ?? "";
                targets[id] = label;
            }
        }

        return targets;
    }

    /// <summary>
    /// Detect exec-configuration actions by scanning output paths for "exec" in the
    /// config directory (e.g. "bazel-out/k8-opt-exec/bin/...").
    /// </summary>
    private static HashSet<string> DetectExecConfigurations(JsonElement root)
    {
        var execConfigs = new HashSet<string>();
        if (!root.TryGetProperty("actions", out var actions))
            return execConfigs;

        foreach (var action in actions.EnumerateArray())
        {
            var configId = action.GetProperty("configurationId").ToString();
            var args = ParseArguments(action);
            foreach (var arg in args)
            {
                if (arg.StartsWith("/out:") || arg.StartsWith("-out:"))
                {
                    var outPath = arg[(arg.IndexOf(':') + 1)..];
                    // bazel-out/<CONFIG_PREFIX>/bin/...
                    var parts = outPath.Split('/');
                    if (parts.Length >= 3 && parts[0] == "bazel-out" &&
                        parts[1].Contains("exec", StringComparison.OrdinalIgnoreCase))
                    {
                        execConfigs.Add(configId);
                    }

                    break;
                }
            }
        }

        return execConfigs;
    }

    private static List<string> ParseArguments(JsonElement action)
    {
        var args = new List<string>();
        if (action.TryGetProperty("arguments", out var argsArray))
        {
            foreach (var arg in argsArray.EnumerateArray())
            {
                args.Add(arg.GetString() ?? "");
            }
        }

        return args;
    }
}
