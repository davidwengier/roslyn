// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CommonLanguageServerProtocol.Framework;

namespace Microsoft.CodeAnalysis.ExternalAccess.Razor.Cohost.Handlers;

internal sealed class EmptyLogger : ILspLogger
{
    public void LogStartContext(string message, params object[] @params)
    {
    }

    public void LogEndContext(string message, params object[] @params)
    {
    }

    public void LogInformation(string message, params object[] @params)
    {
    }

    public void LogWarning(string message, params object[] @params)
    {
    }

    public void LogError(string message, params object[] @params)
    {
    }

    public void LogException(Exception exception, string? message = null, params object[] @params)
    {
    }
}
