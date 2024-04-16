// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Highlighting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.ExternalAccess.Razor.Cohost.Handlers;

[Export(typeof(IDocumentHighlightService))]
[Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal class DocumentHighlightService(IGlobalOptionService globalOptionService, IHighlightingService highlightingService) : IDocumentHighlightService
{
    public async Task<RazorHighlight[]?> GetHighlightsAsync(Document document, LinePosition linePosition, CancellationToken cancellationToken)
    {
        var result = await DocumentHighlightHelper.GetHighlightsAsync(globalOptionService, highlightingService, document, linePosition, cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        var convertedHighlights = result.SelectAsArray(highlight => new RazorHighlight
        {
            Kind = (RazorDocumentHighlightKind)highlight.Kind,
            Range = new RazorRange
            {
                Start = new RazorPosition { Line = highlight.Range.Start.Line, Character = highlight.Range.Start.Character },
                End = new RazorPosition { Line = highlight.Range.End.Line, Character = highlight.Range.End.Character }
            }
        });

        return [.. convertedHighlights];
    }
}

internal class RazorHighlight
{
    public required RazorRange Range { get; set; }
    public RazorDocumentHighlightKind Kind { get; set; }
}

internal class RazorRange
{
    public required RazorPosition Start { get; set; }
    public required RazorPosition End { get; set; }
}

internal class RazorPosition
{
    public int Line { get; set; }
    public int Character { get; set; }
}

internal enum RazorDocumentHighlightKind
{
    Text = 1,
    Read = 2,
    Write = 3,
}

internal interface IDocumentHighlightService
{
    Task<RazorHighlight[]?> GetHighlightsAsync(Document document, LinePosition linePosition, CancellationToken cancellationToken);
}
