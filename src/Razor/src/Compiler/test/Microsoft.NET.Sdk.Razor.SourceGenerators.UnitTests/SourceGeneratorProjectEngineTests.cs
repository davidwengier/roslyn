// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis.Razor.Compiler.CSharp;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators;

public sealed class SourceGeneratorProjectEngineTests
{
    // A duplicate attribute makes ComponentMarkupDiagnosticPass add a diagnostic to the markup
    // attribute node, and stops ComponentMarkupBlockPass collapsing the markup away, so the node
    // survives to be visited again if the remaining phases are replayed over the same tree.
    private const string DuplicateAttributeSource = "<a href=\"one\" href=\"two\">Link</a>";

    private static SourceGeneratorProjectEngine CreateProjectEngine(string source, out SourceGeneratorProjectItem projectItem)
    {
        var additionalText = new TestAdditionalText("Pages/Index.razor", SourceText.From(source, Encoding.UTF8));

        projectItem = new SourceGeneratorProjectItem(
            basePath: "/",
            filePath: "/Pages/Index.razor",
            relativePhysicalPath: "Pages/Index.razor",
            fileKind: RazorFileKind.Component,
            additionalText: additionalText,
            cssScope: null);

        var fileSystem = new VirtualRazorProjectFileSystem();
        fileSystem.Add(projectItem);

        var projectEngine = RazorProjectEngine.Create(RazorConfiguration.Default, fileSystem, b =>
        {
            b.SetRootNamespace("MyApp");
            b.Features.Add(new DefaultUtf8WriteLiteralFeature());
        });

        return new SourceGeneratorProjectEngine(projectEngine);
    }

    /// <summary>
    ///  Runs the phases the generator runs before <see cref="SourceGeneratorProjectEngine.ProcessRemaining"/>,
    ///  which is the point where the incremental pipeline caches the document.
    /// </summary>
    private static SourceGeneratorRazorCodeDocument ProcessUpToCodeGeneration(SourceGeneratorProjectEngine projectEngine, SourceGeneratorProjectItem projectItem)
    {
        var document = projectEngine.ProcessInitialParse(projectItem, CancellationToken.None);
        document = projectEngine.ProcessTagHelpers(document, TagHelperCollection.Empty, checkForIdempotency: false, CancellationToken.None);
        document = projectEngine.ProcessTagHelpers(document, TagHelperCollection.Empty, checkForIdempotency: true, CancellationToken.None);
        return document;
    }

    private static SourceGeneratorRazorCodeDocument ProcessRemaining(SourceGeneratorProjectEngine projectEngine, SourceGeneratorRazorCodeDocument document)
        => projectEngine.ProcessRemaining(document, DefaultUtf8WriteLiteralFeature.Utf8SupportMap.Empty, CancellationToken.None);

    /// <summary>
    ///  Collects every diagnostic on every node in the tree, duplicates included.
    ///  <see cref="IntermediateNodeExtensions.GetAllDiagnostics"/> collects into a hash set, so
    ///  duplicates are invisible in the emitted C# document -- they have to be read off the nodes themselves.
    /// </summary>
    private static List<string> GetNodeDiagnostics(SourceGeneratorRazorCodeDocument document)
    {
        var diagnostics = new List<string>();

        Walk(document.CodeDocument.GetRequiredDocumentNode());

        return diagnostics;

        void Walk(IntermediateNode node)
        {
            foreach (var diagnostic in node.Diagnostics)
            {
                diagnostics.Add($"{node.GetType().Name}: {diagnostic.Id}");
            }

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }
    }

    [Fact, WorkItem("https://devdiv.visualstudio.com/DevDiv/_workitems/edit/3052471")]
    public void ProcessRemaining_DoesNotMutateTheDocumentItWasGiven()
    {
        var projectEngine = CreateProjectEngine(DuplicateAttributeSource, out var projectItem);
        var document = ProcessUpToCodeGeneration(projectEngine, projectItem);

        var before = GetNodeDiagnostics(document);

        ProcessRemaining(projectEngine, document);

        // ProcessRemaining runs the remaining phases straight over the node it was handed, and those phases
        // mutate in place, so the caller's document comes back changed. ProcessTagHelpers clones before it
        // does the same thing; ProcessRemaining never got the same treatment.
        Assert.Equal(before, GetNodeDiagnostics(document));
    }

    [Fact, WorkItem("https://devdiv.visualstudio.com/DevDiv/_workitems/edit/3052471")]
    public void ProcessRemaining_ReplayedOnSameDocument_DoesNotReapplyDiagnostics()
    {
        var projectEngine = CreateProjectEngine(DuplicateAttributeSource, out var projectItem);
        var document = ProcessUpToCodeGeneration(projectEngine, projectItem);

        var first = GetNodeDiagnostics(ProcessRemaining(projectEngine, document));

        // The pipeline hands the same cached document back to ProcessRemaining whenever the UTF-8 support
        // map changes, so replaying has to be idempotent. It isn't: because the tree was mutated in place
        // the second run re-diagnoses nodes the first run already diagnosed.
        var second = GetNodeDiagnostics(ProcessRemaining(projectEngine, document));

        Assert.Equal(first, second);
    }
}
