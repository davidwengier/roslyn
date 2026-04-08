#!/usr/bin/env dotnet
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

try
{
    if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        throw new InvalidOperationException("Usage: dotnet run --file prepare.cs -- <source-root>");

    var sourceRoot = Path.GetFullPath(args[0]);
    if (!Directory.Exists(sourceRoot))
        throw new InvalidOperationException($"Source root '{sourceRoot}' does not exist.");

    // The prepare step should only ever run against the cloned git repo.
    var gitDirectory = Path.Combine(sourceRoot, ".git");
    if (!Directory.Exists(gitDirectory) && !File.Exists(gitDirectory))
        throw new InvalidOperationException($"'{sourceRoot}' is not a git repository.");

    Console.WriteLine($"Preparing Razor source repo at '{sourceRoot}'.");

    // Keep the clone clean so later prep work can make deliberate, reviewable changes.
    var status = await RunGitAsync(sourceRoot, "status", "--short", "--untracked-files=no").ConfigureAwait(false);
    if (!string.IsNullOrWhiteSpace(status))
        throw new InvalidOperationException("The source clone is not clean. Preparation expects a clean checkout.");

    // Record exactly what commit we are preparing so the run log is easy to audit.
    var head = await RunGitAsync(sourceRoot, "rev-parse", "--short", "HEAD").ConfigureAwait(false);
    Console.WriteLine($"Razor source HEAD: {head.Trim()}");

    // Razor does not need any repo-specific mutations here yet.
    Console.WriteLine("No Razor-specific preparation changes are required yet.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start 'git'.");

    var standardOutputTask = process.StandardOutput.ReadToEndAsync();
    var standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);

    var output = (await standardOutputTask.ConfigureAwait(false)) + (await standardErrorTask.ConfigureAwait(false));
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed:{Environment.NewLine}{output.Trim()}");

    return output;
}
