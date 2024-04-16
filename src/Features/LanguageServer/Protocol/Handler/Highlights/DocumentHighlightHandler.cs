// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Highlighting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler
{
    [ExportCSharpVisualBasicStatelessLspService(typeof(DocumentHighlightsHandler)), Shared]
    [Method(Methods.TextDocumentDocumentHighlightName)]
    internal class DocumentHighlightsHandler : ILspServiceDocumentRequestHandler<TextDocumentPositionParams, DocumentHighlight[]?>
    {
        private readonly IHighlightingService _highlightingService;
        private readonly IGlobalOptionService _globalOptions;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public DocumentHighlightsHandler(IHighlightingService highlightingService, IGlobalOptionService globalOptions)
        {
            _highlightingService = highlightingService;
            _globalOptions = globalOptions;
        }

        public bool MutatesSolutionState => false;
        public bool RequiresLSPSolution => true;

        public TextDocumentIdentifier GetTextDocumentIdentifier(TextDocumentPositionParams request) => request.TextDocument;

        public Task<DocumentHighlight[]?> HandleRequestAsync(TextDocumentPositionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            var document = context.Document;
            if (document == null)
                return SpecializedTasks.Null<DocumentHighlight[]>();

            var linePosition = ProtocolConversions.PositionToLinePosition(request.Position);

            return DocumentHighlightHelper.GetHighlightsAsync(_globalOptions, _highlightingService, document, linePosition, cancellationToken);
        }
    }
}
