// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.Bazel;
using static Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.Bazel.BazelAqueryParser;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class BazelAqueryParserTests
{
    private const string WorkspaceRoot = "/home/user/myproject";

    [Fact]
    public void EmptyJson_ReturnsEmpty()
    {
        var json = "{}";
        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void NoActions_ReturnsEmpty()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ]
        }
        """;
        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void NonCSharpAction_IsSkipped()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "Javac",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": ["/usr/bin/javac", "Foo.java"]
                }
            ]
        }
        """;
        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void BasicCSharpAction_ParsedCorrectly()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:mylib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/mylib.dll",
                        "/target:library",
                        "/noconfig",
                        "src/Class1.cs",
                        "src/Class2.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);

        Assert.Single(result);
        var project = result[0];
        Assert.Equal("//src:mylib", project.TargetLabel);
        Assert.Equal("mylib", project.AssemblyName);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(WorkspaceRoot, "bazel-out/k8-fastbuild/bin/src/mylib.dll")),
            project.OutputPath);

        Assert.Equal(2, project.SourceFiles.Length);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(WorkspaceRoot, "src/Class1.cs")),
            project.SourceFiles[0]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(WorkspaceRoot, "src/Class2.cs")),
            project.SourceFiles[1]);

        // CommandLineArgs should have /out: and /target: and /noconfig but NOT source files
        Assert.Contains(project.CommandLineArgs, a => a.StartsWith("/out:"));
        Assert.Contains(project.CommandLineArgs, a => a == "/target:library");
        Assert.Contains(project.CommandLineArgs, a => a == "/noconfig");
        Assert.DoesNotContain(project.CommandLineArgs, a => a.EndsWith(".cs"));
    }

    [Fact]
    public void References_ResolvedToAbsolute()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:app" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/app.dll",
                        "/r:bazel-out/k8-fastbuild/bin/src/mylib.dll",
                        "/reference:bazel-out/k8-fastbuild/bin/external/nuget/System.Text.Json.dll",
                        "src/Program.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];

        // Both /r: and /reference: forms should be converted to absolute /r: format
        var refs = project.CommandLineArgs.Where(a => a.StartsWith("/r:")).ToList();
        Assert.Equal(2, refs.Count);
        foreach (var refArg in refs)
        {
            var refPath = refArg["/r:".Length..];
            Assert.True(Path.IsPathRooted(refPath), $"Reference path should be absolute: {refPath}");
        }
    }

    [Fact]
    public void Analyzers_ResolvedToAbsolute()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "/analyzer:bazel-out/k8-fastbuild/bin/external/analyzers/MyAnalyzer.dll",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];

        var analyzerArg = project.CommandLineArgs.Single(a => a.StartsWith("/analyzer:"));
        var analyzerPath = analyzerArg["/analyzer:".Length..];
        Assert.True(Path.IsPathRooted(analyzerPath), $"Analyzer path should be absolute: {analyzerPath}");
    }

    [Fact]
    public void AdditionalFiles_TrackedSeparately()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "/additionalfile:src/BannedSymbols.txt",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];

        // AdditionalFiles array should contain the file
        Assert.Single(project.AdditionalFiles);
        Assert.True(Path.IsPathRooted(project.AdditionalFiles[0]));

        // CommandLineArgs should also contain the /additionalfile: flag
        Assert.Contains(project.CommandLineArgs, a => a.StartsWith("/additionalfile:"));
    }

    [Fact]
    public void AnalyzerConfigFiles_TrackedSeparately()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "/analyzerconfig:src/.editorconfig",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];

        Assert.Single(project.AnalyzerConfigFiles);
        Assert.True(Path.IsPathRooted(project.AnalyzerConfigFiles[0]));
        Assert.Contains(project.CommandLineArgs, a => a.StartsWith("/analyzerconfig:"));
    }

    [Fact]
    public void ExecConfiguration_IsSkipped()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//tools:codegen" },
                { "id": "2", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg-exec",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-opt-exec/bin/tools/codegen.dll",
                        "tools/CodeGen.cs"
                    ]
                },
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "2",
                    "configurationId": "cfg-target",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);

        // Only the non-exec target should be returned
        Assert.Single(result);
        Assert.Equal("//src:lib", result[0].TargetLabel);
    }

    [Fact]
    public void DuplicateTargetLabels_Deduped()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "src/Foo.cs"
                    ]
                },
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        Assert.Single(result);
    }

    [Fact]
    public void ActionWithoutOut_ReturnsNull()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/target:library",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        // Without /out:, assemblyName is null and the action is skipped
        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void MultipleTargets_AllParsed()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src/core:core" },
                { "id": "2", "label": "//src/web:web" },
                { "id": "3", "label": "//src/cli:cli" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/core/core.dll",
                        "src/core/Core.cs"
                    ]
                },
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "2",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/web/web.dll",
                        "/r:bazel-out/k8-fastbuild/bin/src/core/core.dll",
                        "src/web/Server.cs"
                    ]
                },
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "3",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/cli/cli.dll",
                        "/r:bazel-out/k8-fastbuild/bin/src/core/core.dll",
                        "src/cli/Main.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        Assert.Equal(3, result.Length);
        Assert.Equal("//src/core:core", result[0].TargetLabel);
        Assert.Equal("//src/web:web", result[1].TargetLabel);
        Assert.Equal("//src/cli:cli", result[2].TargetLabel);
    }

    [Fact]
    public void AbsolutePaths_PreservedAsIs()
    {
        // Note: Bazel always emits relative source file paths in aquery output.
        // Absolute paths starting with / are only in flag values like /out: and /r:.
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:/absolute/output/lib.dll",
                        "/r:/absolute/refs/System.dll",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];
        Assert.Equal("/absolute/output/lib.dll", project.OutputPath);

        var refArg = project.CommandLineArgs.Single(a => a.StartsWith("/r:"));
        Assert.Equal("/r:/absolute/refs/System.dll", refArg);

        Assert.Single(project.SourceFiles);
        Assert.True(Path.IsPathRooted(project.SourceFiles[0]));
    }

    [Fact]
    public void DashPrefixFlags_RecognizedCorrectly()
    {
        // rules_dotnet may emit flags with - prefix instead of /
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "-out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "-r:bazel-out/k8-fastbuild/bin/deps/dep.dll",
                        "-analyzer:bazel-out/k8-fastbuild/bin/analyzers/a.dll",
                        "-additionalfile:src/extra.txt",
                        "-analyzerconfig:src/.editorconfig",
                        "-ruleset:src/rules.ruleset",
                        "-target:library",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];

        Assert.Equal("lib", project.AssemblyName);
        Assert.NotNull(project.OutputPath);
        Assert.Single(project.SourceFiles);
        Assert.Single(project.AdditionalFiles);
        Assert.Single(project.AnalyzerConfigFiles);
        Assert.Contains(project.CommandLineArgs, a => a.StartsWith("/r:"));
        Assert.Contains(project.CommandLineArgs, a => a.StartsWith("/analyzer:"));
        Assert.Contains(project.CommandLineArgs, a => a.StartsWith("/additionalfile:"));
        Assert.Contains(project.CommandLineArgs, a => a.StartsWith("/analyzerconfig:"));
        Assert.Contains(project.CommandLineArgs, a => a.StartsWith("/ruleset:"));
        Assert.Contains(project.CommandLineArgs, a => a == "-target:library");
    }

    [Fact]
    public void Ruleset_ResolvedToAbsolute()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "/ruleset:src/MyRules.ruleset",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];

        var rulesetArg = project.CommandLineArgs.Single(a => a.StartsWith("/ruleset:"));
        var rulesetPath = rulesetArg["/ruleset:".Length..];
        Assert.True(Path.IsPathRooted(rulesetPath), $"Ruleset path should be absolute: {rulesetPath}");
    }

    [Fact]
    public void CsxFiles_TreatedAsSources()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//scripts:gen" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/scripts/gen.dll",
                        "scripts/Generate.csx"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];

        Assert.Single(project.SourceFiles);
        Assert.EndsWith(".csx", project.SourceFiles[0]);
    }

    [Fact]
    public void CompilerExecutable_NotIncludedInSourceFiles()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" }
            ],
            "actions": [
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/some/path/to/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "src/Foo.cs"
                    ]
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        var project = result[0];

        // The compiler executable should not appear in source files.
        // It starts with '/' so the parser treats it as a flag in CommandLineArgs,
        // which is harmless — Roslyn's command line parser ignores unrecognized flags.
        Assert.DoesNotContain(project.SourceFiles, f => f.Contains("csc"));
        Assert.Single(project.SourceFiles);
    }

    [Fact]
    public void MixedActions_OnlyCSharpCompileParsed()
    {
        var json = """
        {
            "targets": [
                { "id": "1", "label": "//src:lib" },
                { "id": "2", "label": "//proto:protos" }
            ],
            "actions": [
                {
                    "mnemonic": "GenProto",
                    "targetId": "2",
                    "configurationId": "cfg1",
                    "arguments": ["/usr/bin/protoc", "proto/foo.proto"]
                },
                {
                    "mnemonic": "CSharpCompile",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": [
                        "/usr/bin/csc",
                        "/out:bazel-out/k8-fastbuild/bin/src/lib.dll",
                        "src/Foo.cs"
                    ]
                },
                {
                    "mnemonic": "Middleman",
                    "targetId": "1",
                    "configurationId": "cfg1",
                    "arguments": []
                }
            ]
        }
        """;

        var result = ParseCSharpCompileActions(json, WorkspaceRoot);
        Assert.Single(result);
        Assert.Equal("//src:lib", result[0].TargetLabel);
    }
}
