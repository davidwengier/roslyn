// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Roslyn.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.TextDocumentContent;

/// <summary>
/// Abstract refresh queue for text document content providers that use the LSP 3.18
/// <c>workspace/textDocumentContent</c> mechanism. Subclasses specify which URI schemes they handle
/// and implement custom change detection logic via <see cref="AbstractRefreshQueue.OnLspSolutionChanged"/>.
/// <para>
/// When a refresh is needed, this queue sends a <c>workspace/textDocumentContent/refresh</c> request
/// for each tracked document whose URI matches one of the registered schemes.
/// </para>
/// </summary>
internal abstract class AbstractTextDocumentContentRefreshQueue(
    IAsynchronousOperationListenerProvider asynchronousOperationListenerProvider,
    LspWorkspaceRegistrationService lspWorkspaceRegistrationService,
    LspWorkspaceManager lspWorkspaceManager,
    IClientLanguageServerManager notificationManager)
    : AbstractRefreshQueue(asynchronousOperationListenerProvider, lspWorkspaceRegistrationService, lspWorkspaceManager, notificationManager)
{
    /// <summary>
    /// Returns the URI schemes this refresh queue handles (e.g. <c>"roslyn-source-generated"</c>).
    /// </summary>
    protected abstract ImmutableArray<string> GetSchemes();

    protected sealed override string GetWorkspaceRefreshName()
        => Methods.WorkspaceTextDocumentContentRefreshName;

    protected sealed override bool? GetRefreshSupport(ClientCapabilities clientCapabilities)
        => clientCapabilities.Workspace?.TextDocumentContent is not null;

    /// <summary>
    /// Subclasses should override <see cref="AbstractRefreshQueue.OnLspSolutionChanged"/> to implement
    /// specific logic for when to enqueue refresh notifications.
    /// </summary>
    protected override void OnLspSolutionChanged(object? sender, WorkspaceChangeEventArgs e)
    {
    }

    /// <summary>
    /// Sends a <c>workspace/textDocumentContent/refresh</c> request for each tracked document
    /// whose URI scheme matches one of the schemes returned by <see cref="GetSchemes"/>.
    /// </summary>
    protected sealed override async ValueTask ProcessBatchAsync(
        ImmutableSegmentedList<DocumentUri?> documentUris,
        CancellationToken cancellationToken)
    {
        var trackedDocuments = LspWorkspaceManager.GetTrackedLspText();
        var schemes = GetSchemes();

        foreach (var kvp in trackedDocuments)
        {
            var uri = kvp.Key;
            if (uri.ParsedUri is { } parsedUri && schemes.Contains(parsedUri.Scheme))
            {
                try
                {
                    await NotificationManager.SendRequestAsync(
                        GetWorkspaceRefreshName(),
                        new TextDocumentContentRefreshParams { Uri = uri },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException)
                {
                    // Connection may be lost during shutdown.
                    return;
                }
            }
        }
    }
}
