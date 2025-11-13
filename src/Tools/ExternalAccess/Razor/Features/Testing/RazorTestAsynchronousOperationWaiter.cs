// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.TestHooks;

namespace Microsoft.CodeAnalysis.ExternalAccess.Razor;

[Export(typeof(RazorTestAsynchronousOperationWaiter))]
[Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class RazorTestAsynchronousOperationWaiter(AsynchronousOperationListenerProvider implementation)
{
    public static void Enable(bool enable)
        => AsynchronousOperationListenerProvider.Enable(enable, diagnostics: null);

    public Task WaitAsync(string featureName)
        => implementation.GetWaiter(featureName).ExpeditedWaitAsync();

    public Task WaitAllAsync(Workspace? workspace, string[]? featureNames = null)
        => implementation.WaitAllAsync(workspace, featureNames);
}
