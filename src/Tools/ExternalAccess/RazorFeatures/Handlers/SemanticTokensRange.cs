// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.LanguageServer.Handler.SemanticTokens;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.ExternalAccess.Razor.Features.Handlers
{
    internal static class SemanticTokensRange
    {
        public static Task<int[]> GetSemanticTokensAsync(
            bool supportsVisualStudioExtensions,
            Document document,
            ImmutableArray<LinePositionSpan> spans,
            ClassificationOptions options,
            CancellationToken cancellationToken)
        {
            var tokens = SemanticTokensHelpers.HandleRequestHelperAsync(
                        supportsVisualStudioExtensions,
                        document,
                        spans,
                        options,
                        cancellationToken);

            // The above call to get semantic tokens may be inaccurate (because we use frozen partial semantics).  Kick
            // off a request to ensure that the OOP side gets a fully up to compilation for this project.  Once it does
            // we can optionally choose to notify our caller to do a refresh if we computed a compilation for a new
            // solution snapshot.
            // TODO: await semanticTokensRefreshQueue.TryEnqueueRefreshComputationAsync(project, cancellationToken).ConfigureAwait(false);
            return tokens;
        }
    }
}
