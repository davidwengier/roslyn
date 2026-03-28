// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.CodeGeneration;

/// <summary>
/// The type of code generation that is happening, or Default if no special treatment is necessary
/// </summary>
internal enum CodeGenerationKind
{
    Default = 0,
    GenerateMethod = 1,
}
