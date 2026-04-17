// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.Bazel;

[ExportCSharpVisualBasicStatelessLspService(typeof(OpenBazelWorkspaceHandler)), Shared]
[Method(MethodName)]
internal sealed class OpenBazelWorkspaceHandler : ILspServiceNotificationHandler<OpenBazelWorkspaceHandler.NotificationParams>
{
    internal const string MethodName = "bazel/openWorkspace";

    private readonly BazelProjectSystem _bazelProjectSystem;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public OpenBazelWorkspaceHandler(BazelProjectSystem bazelProjectSystem)
    {
        _bazelProjectSystem = bazelProjectSystem;
    }

    public bool MutatesSolutionState => false;
    public bool RequiresLSPSolution => false;

    Task INotificationHandler<NotificationParams, RequestContext>.HandleNotificationAsync(
        NotificationParams request, RequestContext requestContext, CancellationToken cancellationToken)
    {
        return _bazelProjectSystem.OpenWorkspaceAsync(
            request.WorkspaceRoot,
            request.BazelPath ?? "bazel",
            request.Config,
            request.Targets is not null ? [.. request.Targets] : ImmutableArray<string>.Empty,
            request.StartupFlags is not null ? [.. request.StartupFlags] : ImmutableArray<string>.Empty,
            cancellationToken);
    }

    internal sealed class NotificationParams
    {
        [JsonPropertyName("workspaceRoot")]
        public required string WorkspaceRoot { get; set; }

        [JsonPropertyName("bazelPath")]
        public string? BazelPath { get; set; }

        [JsonPropertyName("config")]
        public string? Config { get; set; }

        [JsonPropertyName("targets")]
        public string[]? Targets { get; set; }

        [JsonPropertyName("startupFlags")]
        public string[]? StartupFlags { get; set; }
    }
}
