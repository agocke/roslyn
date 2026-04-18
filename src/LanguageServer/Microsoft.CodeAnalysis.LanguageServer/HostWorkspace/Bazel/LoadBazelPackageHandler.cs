// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.Bazel;

[ExportCSharpVisualBasicStatelessLspService(typeof(LoadBazelPackageHandler)), Shared]
[Method(MethodName)]
internal sealed class LoadBazelPackageHandler : ILspServiceNotificationHandler<LoadBazelPackageHandler.NotificationParams>
{
    internal const string MethodName = "bazel/loadPackage";

    private readonly BazelProjectSystem _bazelProjectSystem;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public LoadBazelPackageHandler(BazelProjectSystem bazelProjectSystem)
    {
        _bazelProjectSystem = bazelProjectSystem;
    }

    public bool MutatesSolutionState => false;
    public bool RequiresLSPSolution => false;

    Task INotificationHandler<NotificationParams, RequestContext>.HandleNotificationAsync(
        NotificationParams request, RequestContext requestContext, CancellationToken cancellationToken)
    {
        return _bazelProjectSystem.LoadTargetsForPackageAsync(request.PackagePath, cancellationToken);
    }

    internal sealed class NotificationParams
    {
        [JsonPropertyName("packagePath")]
        public required string PackagePath { get; set; }
    }
}
