﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics
{
    internal static class PullDiagnosticConstants
    {
        public const string TaskItemCustomTag = nameof(TaskItemCustomTag);

        /// <summary>
        /// Diagnostic category to get project diagnostics.
        /// </summary>
        public const string Project = nameof(Project);
        public const string CompilerSyntax = nameof(CompilerSyntax);
        public const string CompilerSemantic = nameof(CompilerSemantic);
        public const string AnalyzerSyntax = nameof(AnalyzerSyntax);
        public const string AnalyzerSemantic = nameof(AnalyzerSemantic);
    }
}
