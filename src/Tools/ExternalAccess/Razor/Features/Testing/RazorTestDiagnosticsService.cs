// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;

namespace Microsoft.CodeAnalysis.ExternalAccess.Razor;

[Export(typeof(RazorTestDiagnosticsService)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal class RazorTestDiagnosticsService(AsynchronousOperationListenerProvider listenerProvider)
{
    public async Task<object> ForceRunCodeAnalysisDiagnosticsAsync(Project project, CancellationToken cancellationToken)
    {
        var diagnosticAnalyzerService = project.Solution.Services.GetRequiredService<IDiagnosticAnalyzerService>();
        return await diagnosticAnalyzerService.ForceRunCodeAnalysisDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false);
    }

    public void InitializeDiagnosticScope(Workspace workspace)
    {
        var globalOptions = workspace.CurrentSolution.Services.ExportProvider.GetService<IGlobalOptionService>();

        globalOptions.SetGlobalOption(SolutionCrawlerOptionsStorage.BackgroundAnalysisScopeOption, LanguageNames.CSharp, BackgroundAnalysisScope.FullSolution);
        globalOptions.SetGlobalOption(SolutionCrawlerOptionsStorage.CompilerDiagnosticsScopeOption, LanguageNames.CSharp, CompilerDiagnosticsScope.FullSolution);
    }

    public async Task WaitForDiagnosticsAsync()
    {
        await listenerProvider.GetWaiter(FeatureAttribute.Workspace).ExpeditedWaitAsync().ConfigureAwait(false);
        await listenerProvider.GetWaiter(FeatureAttribute.SolutionCrawlerLegacy).ExpeditedWaitAsync().ConfigureAwait(false);
        await listenerProvider.GetWaiter(FeatureAttribute.DiagnosticService).ExpeditedWaitAsync().ConfigureAwait(false);
    }
}
