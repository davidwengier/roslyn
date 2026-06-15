// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Razor.Cohost;
using Microsoft.CodeAnalysis.Razor.Logging;
using Microsoft.CodeAnalysis.Razor.Remote;
using Microsoft.CodeAnalysis.Razor.Telemetry;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.VisualStudio.Razor.LanguageClient.Cohost;

internal abstract class CohostSemanticTokensEndpointBase<TRequest>(
    IIncompatibleProjectService incompatibleProjectService,
    IRemoteServiceInvoker remoteServiceInvoker,
    ITelemetryReporter telemetryReporter,
    ILoggerFactory loggerFactory)
    : AbstractCohostDocumentEndpoint<TRequest, SemanticTokens?>(incompatibleProjectService)
    where TRequest : ITextDocumentParams
{
    private readonly IRemoteServiceInvoker _remoteServiceInvoker = remoteServiceInvoker;
    private readonly ITelemetryReporter _telemetryReporter = telemetryReporter;
    private readonly ILogger _logger = loggerFactory.GetOrCreateLogger<CohostSemanticTokensEndpointBase<TRequest>>();

    protected override bool MutatesSolutionState => false;
    protected override bool RequiresLSPSolution => true;

    protected abstract string LspMethodName { get; }

    protected override TextDocumentIdentifier? GetRazorTextDocumentIdentifier(TRequest request)
        => request.TextDocument;

    protected override async Task<SemanticTokens?> HandleRequestAsync(TRequest request, RequestContext context, TextDocument razorDocument, CancellationToken cancellationToken)
    {
        var result = await HandleRequestAsync(request, razorDocument, cancellationToken).ConfigureAwait(false);

        if (result is not null)
        {
            // Roslyn uses frozen semantics for semantic tokens, so it could return results from an older project state.
            // Every time they get a request they queue up a refresh, which will check the project checksums, and if there
            // hasn't been any changes, will no-op. We call into that same logic here to ensure everything is up to date.
            // See: https://github.com/dotnet/roslyn/blob/bb57f4643bb3d52eb7626f9863da177d9e219f1e/src/LanguageServer/Protocol/Handler/SemanticTokens/SemanticTokensHelpers.cs#L48-L52
            var semanticTokensWrapperService = context.GetRequiredService<IRazorSemanticTokensRefreshQueue>();
            await semanticTokensWrapperService.TryEnqueueRefreshComputationAsync(razorDocument.Project, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    protected abstract Task<LinePositionSpan> GetRequestSpanAsync(TRequest request, TextDocument razorDocument, CancellationToken cancellationToken);

    protected override async Task<SemanticTokens?> HandleRequestAsync(TRequest request, TextDocument razorDocument, CancellationToken cancellationToken)
    {
        var span = await GetRequestSpanAsync(request, razorDocument, cancellationToken).ConfigureAwait(false);
        return await HandleRequestAsync(razorDocument, span, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SemanticTokens?> HandleRequestAsync(TextDocument razorDocument, LinePositionSpan span, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        using var _ = _telemetryReporter.TrackLspRequest(LspMethodName, RazorLSPConstants.CohostLanguageServerName, TelemetryThresholds.SemanticTokensRazorTelemetryThreshold, correlationId);

        var tokens = await _remoteServiceInvoker.TryInvokeAsync<IRemoteSemanticTokensService, int[]?>(
            razorDocument.Project.Solution,
            (service, solutionInfo, cancellationToken) => service.GetSemanticTokensDataAsync(solutionInfo, razorDocument.Id, span, correlationId, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (tokens is not null)
        {
            return new SemanticTokens
            {
                Data = tokens
            };
        }

        var logInfo = await CreateEndpointRequestInfoAsync(razorDocument, span, correlationId, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug($"Semantic tokens remote call returned null.{Environment.NewLine}{logInfo}");
        return null;
    }

    private async Task<string> CreateEndpointRequestInfoAsync(TextDocument razorDocument, LinePositionSpan span, Guid correlationId, CancellationToken cancellationToken)
    {
        var sourceText = await razorDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var textVersion = await razorDocument.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
        var checksum = await razorDocument.State.GetChecksumAsync(cancellationToken).ConfigureAwait(false);
        var workspaceVersion = razorDocument.Project.Solution.SolutionStateContentVersion;

        return $"""
            Endpoint method: {LspMethodName}
            Endpoint correlation ID: {correlationId}
            Endpoint requested span: {span}
            Endpoint TextDocument file path: {razorDocument.FilePath}
            Endpoint TextDocument id: {razorDocument.Id}
            Endpoint TextDocument checksum: {checksum}
            Endpoint TextDocument text version: {textVersion}
            Endpoint workspace version: {workspaceVersion}
            Endpoint source length: {sourceText.Length}
            Endpoint source line count: {sourceText.Lines.Count}
            Endpoint span validity: {GetSpanDiagnostic(sourceText, span)}
            """;
    }

    private static string GetSpanDiagnostic(SourceText sourceText, LinePositionSpan span)
    {
        if (!TryGetAbsoluteIndex(sourceText, span.Start, out var start) ||
            !TryGetAbsoluteIndex(sourceText, span.End, out var end) ||
            end < start)
        {
            return "invalid";
        }

        return $"valid, text span {TextSpan.FromBounds(start, end)}";
    }

    private static bool TryGetAbsoluteIndex(SourceText sourceText, LinePosition position, out int absoluteIndex)
    {
        if (position.Line < 0 ||
            position.Character < 0)
        {
            absoluteIndex = 0;
            return false;
        }

        return sourceText.TryGetAbsoluteIndex(position, out absoluteIndex);
    }
}
