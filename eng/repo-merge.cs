#!/usr/bin/env dotnet
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

return await RepoMergeScaffold.MainAsync(args);

static class RepoMergeScaffold
{
    private const string DefaultSourceRepo = "dotnet/razor";
    private const string DefaultSourceBranch = "main";
    private const string DefaultTargetPath = @"src\Razor";
    private const string DefaultStateRoot = @"artifacts\repo-merge";
    private const string DefaultWorkRoot = @"..\repo-merge-work";
    private const int StateSchemaVersion = 1;
    private const string WorkflowVersion = "clone-stage-v1";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly MergeStageDefinition[] s_stageDefinitions =
    [
        new(
            "validate-environment",
            "Validate the repo root, input arguments, and required tooling.",
            ValidateEnvironmentAsync),
        new(
            "prepare-state",
            "Create the persisted state, sentinel, and external work-area layout for the run.",
            PrepareStateAsync),
        new(
            "clone-source",
            "Clone or refresh the source repository in an external work area.",
            CloneSourceAsync),
        new(
            "trim-history",
            "Scaffold placeholder for trimming source history before import.",
            TrimHistoryAsync),
        new(
            "merge-into-roslyn",
            "Scaffold placeholder for importing the prepared repo into the target path.",
            MergeIntoRoslynAsync),
        new(
            "finalize-scaffold",
            "Write the scaffold summary and next-step instructions.",
            FinalizeScaffoldAsync),
    ];

    public static async Task<int> MainAsync(string[] args)
    {
        var sourceRepoOption = new Option<string?>("--source-repo")
        {
            Description = $"Source repository to merge (default: '{DefaultSourceRepo}'). Accepts owner/repo or a local path.",
        };

        var sourceBranchOption = new Option<string?>("--source-branch")
        {
            Description = $"Source branch to use (default: '{DefaultSourceBranch}').",
        };

        var targetPathOption = new Option<string?>("--target-path")
        {
            Description = $"Destination path inside roslyn (default: '{DefaultTargetPath}').",
        };

        var stateRootOption = new Option<string?>("--state-root")
        {
            Description = $"Directory for persisted run state (default: '{DefaultStateRoot}').",
        };

        var workRootOption = new Option<string?>("--work-root")
        {
            Description = $"Directory for external working repos (default: '{DefaultWorkRoot}'). Must resolve outside the roslyn repo.",
        };

        var runNameOption = new Option<string?>("--run-name")
        {
            Description = "Name for the persisted run folder. If omitted, one is derived from the source repo and target path.",
        };

        var stageOption = new Option<string?>("--stage")
        {
            Description = "Run just one named stage. Use --list-stages to view valid names.",
        };

        var startAtOption = new Option<string?>("--start-at")
        {
            Description = "Start execution at the specified stage.",
        };

        var stopAfterOption = new Option<string?>("--stop-after")
        {
            Description = "Stop after the specified stage completes.",
        };

        var listStagesOption = new Option<bool>("--list-stages")
        {
            Description = "List the available stages and exit.",
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Run in dry-run mode. The scaffold only writes its own state/log artifacts.",
        };

        var resumeOption = new Option<bool>("--resume")
        {
            Description = "Resume a previously-started run from its persisted state.",
        };

        var rerunOption = new Option<bool>("--rerun")
        {
            Description = "Execute the selected stages even if they are already marked complete.",
        };

        var resetOption = new Option<bool>("--reset")
        {
            Description = "Delete any existing state for the selected run before starting over.",
        };

        var rootCommand = new RootCommand("Run the repeatable repo-merge workflow with persisted stage state.")
        {
            sourceRepoOption,
            sourceBranchOption,
            targetPathOption,
            stateRootOption,
            workRootOption,
            runNameOption,
            stageOption,
            startAtOption,
            stopAfterOption,
            listStagesOption,
            dryRunOption,
            resumeOption,
            rerunOption,
            resetOption,
        };

        if (args.Any(static arg => arg is "--help" or "-h" or "/?"))
            return rootCommand.Parse(args).Invoke();

        var parseResult = rootCommand.Parse(args);
        var parseExitCode = parseResult.Invoke();
        if (parseExitCode != 0)
            return parseExitCode;

        var settings = new MergeSettings(
            SourceRepo: parseResult.GetValue(sourceRepoOption) ?? DefaultSourceRepo,
            SourceBranch: parseResult.GetValue(sourceBranchOption) ?? DefaultSourceBranch,
            TargetPath: parseResult.GetValue(targetPathOption) ?? DefaultTargetPath,
            StateRoot: parseResult.GetValue(stateRootOption) ?? DefaultStateRoot,
            WorkRoot: parseResult.GetValue(workRootOption) ?? DefaultWorkRoot,
            RunName: parseResult.GetValue(runNameOption),
            Stage: parseResult.GetValue(stageOption),
            StartAt: parseResult.GetValue(startAtOption),
            StopAfter: parseResult.GetValue(stopAfterOption),
            ListStages: parseResult.GetValue(listStagesOption),
            DryRun: parseResult.GetValue(dryRunOption),
            Resume: parseResult.GetValue(resumeOption),
            Rerun: parseResult.GetValue(rerunOption),
            Reset: parseResult.GetValue(resetOption));

        if (settings.ListStages)
        {
            PrintStages();
            return 0;
        }

        try
        {
            return await RunAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(MergeSettings settings)
    {
        var repoRoot = GetRepoRoot();
        var runName = string.IsNullOrWhiteSpace(settings.RunName)
            ? GetDefaultRunName(settings.SourceRepo, settings.TargetPath)
            : SanitizePathSegment(settings.RunName);
        var stateRoot = GetAbsolutePath(repoRoot, settings.StateRoot);
        var workRoot = GetAbsolutePath(repoRoot, settings.WorkRoot);
        EnsurePathIsOutsideRepo(repoRoot, workRoot, "--work-root");
        var runDirectory = Path.Combine(stateRoot, runName);
        var workDirectory = Path.Combine(workRoot, runName);

        if (settings.Reset && Directory.Exists(runDirectory))
            Directory.Delete(runDirectory, recursive: true);
        if (settings.Reset && Directory.Exists(workDirectory))
            Directory.Delete(workDirectory, recursive: true);

        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(Path.Combine(runDirectory, "logs"));
        Directory.CreateDirectory(workDirectory);

        var logger = new RunLogger(Path.Combine(runDirectory, "logs", "run.log"));
        logger.Info($"Starting repo-merge scaffold '{runName}' (workflow version {WorkflowVersion}).");

        var executionPlan = CreateExecutionPlan(settings);
        var statePath = Path.Combine(runDirectory, "state.json");
        var stateExists = File.Exists(statePath);

        if (stateExists && !settings.Resume && !settings.Rerun)
        {
            throw new InvalidOperationException(
                $"State already exists for run '{runName}' at '{runDirectory}'. " +
                "Use --resume to continue, --rerun to execute completed stages again, or --reset to start over.");
        }

        var state = stateExists
            ? await LoadStateAsync(statePath).ConfigureAwait(false)
            : CreateState(settings, repoRoot, runName, runDirectory, executionPlan);

        EnsureCompatibleState(state, settings, repoRoot, runDirectory);
        SyncStageMetadata(state);
        RecoverCompletedStagesFromSentinels(state, runDirectory);

        state.SourceRepo = settings.SourceRepo;
        state.SourceBranch = settings.SourceBranch;
        state.TargetPath = settings.TargetPath;
        state.StateRoot = stateRoot;
        state.WorkRoot = workRoot;
        state.RunName = runName;
        state.RunDirectory = runDirectory;
        state.WorkDirectory = workDirectory;
        state.RepoRoot = repoRoot;
        state.SourceRemoteUri = ResolveSourceRepositoryUri(settings.SourceRepo, repoRoot);
        state.SourceCloneDirectory = Path.Combine(workDirectory, "source");
        state.TrimmedDirectory = Path.Combine(workDirectory, "trimmed");
        state.ImportPreviewDirectory = Path.Combine(workDirectory, "import-preview");
        state.WorkflowVersion = WorkflowVersion;
        state.DryRun = settings.DryRun;
        state.SelectedStartStage = executionPlan.StartStageName;
        state.SelectedStopStage = executionPlan.StopStageName;
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await SaveStateAsync(statePath, state).ConfigureAwait(false);

        logger.Info($"State file: {statePath}");
        logger.Info($"Selected stages: {executionPlan.StartStageName} -> {executionPlan.StopStageName}");
        if (settings.DryRun)
            logger.Info("Running in dry-run mode.");

        var context = new StageContext(
            Settings: settings,
            RepoRoot: repoRoot,
            RunDirectory: runDirectory,
            StatePath: statePath,
            State: state,
            Logger: logger);

        for (var i = executionPlan.StartIndex; i <= executionPlan.StopIndex; i++)
        {
            var definition = s_stageDefinitions[i];
            var record = GetStageState(state, definition.Name);

            if (record.Status == StageStatus.Completed && !settings.Rerun)
            {
                logger.Info($"Skipping completed stage '{definition.Name}'.");
                continue;
            }

            record.Status = StageStatus.InProgress;
            record.AttemptCount++;
            record.StartedUtc = DateTimeOffset.UtcNow;
            record.LastMessage = null;
            state.CurrentStage = definition.Name;
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveStateAsync(statePath, state).ConfigureAwait(false);

            try
            {
                logger.Info($"Starting stage '{definition.Name}': {definition.Description}");
                var summary = await definition.ExecuteAsync(context).ConfigureAwait(false);

                record.Status = StageStatus.Completed;
                record.CompletedUtc = DateTimeOffset.UtcNow;
                record.LastMessage = summary;
                state.CurrentStage = string.Empty;
                state.LastCompletedStage = definition.Name;
                state.UpdatedUtc = DateTimeOffset.UtcNow;

                await WriteSentinelAsync(runDirectory, definition, record).ConfigureAwait(false);
                await SaveStateAsync(statePath, state).ConfigureAwait(false);

                logger.Info($"Completed stage '{definition.Name}'.");
            }
            catch (Exception ex)
            {
                record.Status = StageStatus.Failed;
                record.LastMessage = ex.Message;
                state.CurrentStage = definition.Name;
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                await SaveStateAsync(statePath, state).ConfigureAwait(false);

                logger.Error($"Stage '{definition.Name}' failed: {ex.Message}");
                return 1;
            }
        }

        logger.Info("Repo-merge scaffold completed successfully.");
        return 0;
    }

    private static ExecutionPlan CreateExecutionPlan(MergeSettings settings)
    {
        var startStageName = settings.Stage ?? settings.StartAt ?? s_stageDefinitions[0].Name;
        var stopStageName = settings.Stage ?? settings.StopAfter ?? s_stageDefinitions[^1].Name;
        var startIndex = GetStageIndex(startStageName);
        var stopIndex = GetStageIndex(stopStageName);

        if (startIndex > stopIndex)
        {
            throw new InvalidOperationException(
                $"The selected start stage '{startStageName}' comes after the stop stage '{stopStageName}'.");
        }

        return new ExecutionPlan(startIndex, stopIndex, s_stageDefinitions[startIndex].Name, s_stageDefinitions[stopIndex].Name);
    }

    private static void PrintStages()
    {
        Console.WriteLine("Available repo-merge stages:");
        foreach (var stage in s_stageDefinitions)
            Console.WriteLine($"  {stage.Name,-20} {stage.Description}");
    }

    private static int GetStageIndex(string stageName)
    {
        var normalizedName = NormalizeStageName(stageName);
        for (var i = 0; i < s_stageDefinitions.Length; i++)
        {
            if (NormalizeStageName(s_stageDefinitions[i].Name) == normalizedName)
                return i;
        }

        throw new InvalidOperationException(
            $"Unknown stage '{stageName}'. Use --list-stages to view the supported stage names.");
    }

    private static string NormalizeStageName(string stageName)
        => stageName.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

    private static MergeRunState CreateState(MergeSettings settings, string repoRoot, string runName, string runDirectory, ExecutionPlan executionPlan)
    {
        var state = new MergeRunState
        {
            SchemaVersion = StateSchemaVersion,
            WorkflowVersion = WorkflowVersion,
            RunName = runName,
            SourceRepo = settings.SourceRepo,
            SourceBranch = settings.SourceBranch,
            TargetPath = settings.TargetPath,
            StateRoot = GetAbsolutePath(repoRoot, settings.StateRoot),
            RunDirectory = runDirectory,
            RepoRoot = repoRoot,
            DryRun = settings.DryRun,
            SelectedStartStage = executionPlan.StartStageName,
            SelectedStopStage = executionPlan.StopStageName,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        SyncStageMetadata(state);
        return state;
    }

    private static void SyncStageMetadata(MergeRunState state)
    {
        foreach (var definition in s_stageDefinitions)
        {
            var stage = GetStageState(state, definition.Name);
            stage.Description = definition.Description;
        }
    }

    private static void RecoverCompletedStagesFromSentinels(MergeRunState state, string runDirectory)
    {
        var sentinelsDirectory = Path.Combine(runDirectory, "sentinels");
        if (!Directory.Exists(sentinelsDirectory))
            return;

        foreach (var definition in s_stageDefinitions)
        {
            var sentinelPath = Path.Combine(sentinelsDirectory, $"{definition.Name}.done");
            if (!File.Exists(sentinelPath))
                continue;

            var stage = GetStageState(state, definition.Name);
            if (stage.Status == StageStatus.Completed)
                continue;

            stage.Status = StageStatus.Completed;
            stage.CompletedUtc ??= File.GetLastWriteTimeUtc(sentinelPath);
            stage.LastMessage ??= "Recovered completion from sentinel file.";
            state.LastCompletedStage = definition.Name;
        }
    }

    private static void EnsureCompatibleState(MergeRunState state, MergeSettings settings, string repoRoot, string runDirectory)
    {
        if (state.SchemaVersion != StateSchemaVersion || !string.Equals(state.WorkflowVersion, WorkflowVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Existing state at '{runDirectory}' was created for workflow version '{state.WorkflowVersion}' " +
                $"(schema {state.SchemaVersion}). Use --reset or choose a new --run-name.");
        }

        ValidateMatchingSetting(state.SourceRepo, settings.SourceRepo, nameof(settings.SourceRepo));
        ValidateMatchingSetting(state.SourceBranch, settings.SourceBranch, nameof(settings.SourceBranch));
        ValidateMatchingSetting(state.TargetPath, settings.TargetPath, nameof(settings.TargetPath));
        ValidateMatchingSetting(state.RepoRoot, repoRoot, nameof(repoRoot));
        ValidateMatchingSetting(state.WorkRoot, GetAbsolutePath(repoRoot, settings.WorkRoot), nameof(settings.WorkRoot));
    }

    private static void ValidateMatchingSetting(string existingValue, string currentValue, string name)
    {
        if (!string.IsNullOrWhiteSpace(existingValue) && !string.Equals(existingValue, currentValue, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Existing state was created with a different {name} value ('{existingValue}' vs '{currentValue}'). " +
                "Use --reset or a different --run-name.");
        }
    }

    private static StageState GetStageState(MergeRunState state, string stageName)
    {
        foreach (var stage in state.Stages)
        {
            if (string.Equals(stage.Name, stageName, StringComparison.OrdinalIgnoreCase))
                return stage;
        }

        var newStage = new StageState
        {
            Name = stageName,
            Status = StageStatus.Pending,
        };

        state.Stages.Add(newStage);
        return newStage;
    }

    private static async Task WriteSentinelAsync(string runDirectory, MergeStageDefinition definition, StageState record)
    {
        var sentinelsDirectory = Path.Combine(runDirectory, "sentinels");
        Directory.CreateDirectory(sentinelsDirectory);

        var content = $"""
            Stage: {definition.Name}
            Description: {definition.Description}
            CompletedUtc: {record.CompletedUtc:O}
            Attempts: {record.AttemptCount}
            Summary: {record.LastMessage}
            """;

        await File.WriteAllTextAsync(Path.Combine(sentinelsDirectory, $"{definition.Name}.done"), content).ConfigureAwait(false);
    }

    private static async Task<MergeRunState> LoadStateAsync(string statePath)
    {
        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<MergeRunState>(stream, s_jsonOptions).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Could not deserialize '{statePath}'.");
    }

    private static async Task SaveStateAsync(string statePath, MergeRunState state)
    {
        await using var stream = File.Create(statePath);
        await JsonSerializer.SerializeAsync(stream, state, s_jsonOptions).ConfigureAwait(false);
    }

    private static async Task<string> ValidateEnvironmentAsync(StageContext context)
    {
        if (!Directory.Exists(Path.Combine(context.RepoRoot, ".git")) && !File.Exists(Path.Combine(context.RepoRoot, ".git")))
            throw new InvalidOperationException("The repo root does not appear to be a git checkout.");

        if (string.IsNullOrWhiteSpace(context.Settings.SourceRepo))
            throw new InvalidOperationException("--source-repo must not be empty.");

        if (string.IsNullOrWhiteSpace(context.Settings.TargetPath))
            throw new InvalidOperationException("--target-path must not be empty.");

        var fullTargetPath = GetAbsolutePath(context.RepoRoot, context.Settings.TargetPath);
        var fullWorkRoot = GetAbsolutePath(context.RepoRoot, context.Settings.WorkRoot);
        if (!IsPathWithinRoot(context.RepoRoot, fullTargetPath))
            throw new InvalidOperationException($"The target path '{context.Settings.TargetPath}' escapes the repo root.");

        if (Path.IsPathRooted(context.Settings.TargetPath))
            throw new InvalidOperationException("--target-path must be relative to the roslyn repo root.");

        EnsurePathIsOutsideRepo(context.RepoRoot, fullWorkRoot, "--work-root");

        if (LooksLikeLocalPath(context.Settings.SourceRepo))
        {
            var fullSourcePath = GetAbsolutePath(context.RepoRoot, context.Settings.SourceRepo);
            if (!Directory.Exists(fullSourcePath))
                throw new InvalidOperationException($"The local source repo path '{fullSourcePath}' does not exist.");
        }
        else if (context.Settings.SourceRepo.Count(static c => c == '/') != 1)
        {
            throw new InvalidOperationException("--source-repo must be in owner/repo format or a valid local path.");
        }

        var gitVersion = await RunProcessAsync("git", ["--version"], context.RepoRoot).ConfigureAwait(false);
        if (gitVersion.ExitCode != 0)
            throw new InvalidOperationException($"`git --version` failed: {gitVersion.Output.Trim()}");

        var status = await RunProcessAsync("git", ["status", "--short", "--untracked-files=no"], context.RepoRoot).ConfigureAwait(false);
        context.Logger.Info($"Resolved target path: {fullTargetPath}");
        context.Logger.Info($"Resolved external work root: {fullWorkRoot}");
        if (!string.IsNullOrWhiteSpace(status.Output))
            context.Logger.Info("Git working tree has existing changes; the scaffold recorded them but does not modify the repo yet.");

        return $"Validated repo root and inputs. Git: {gitVersion.Output.Trim()}";
    }

    private static async Task<string> PrepareStateAsync(StageContext context)
    {
        Directory.CreateDirectory(Path.Combine(context.RunDirectory, "sentinels"));
        Directory.CreateDirectory(Path.Combine(context.RunDirectory, "notes"));
        Directory.CreateDirectory(context.State.WorkDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(context.State.SourceCloneDirectory)!);

        var manifest = new
        {
            workflowVersion = WorkflowVersion,
            stateSchemaVersion = StateSchemaVersion,
            context.Settings.SourceRepo,
            context.Settings.SourceBranch,
            context.Settings.TargetPath,
            context.State.WorkRoot,
            context.State.WorkDirectory,
            context.State.SourceCloneDirectory,
            context.Settings.DryRun,
            selectedStages = new
            {
                start = context.State.SelectedStartStage,
                stop = context.State.SelectedStopStage,
            },
        };

        var manifestPath = Path.Combine(context.RunDirectory, "inputs.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, s_jsonOptions)).ConfigureAwait(false);

        return $"Prepared persisted state under '{context.RunDirectory}' and external work area '{context.State.WorkDirectory}'.";
    }

    private static async Task<string> CloneSourceAsync(StageContext context)
    {
        var sourceDirectory = context.State.SourceCloneDirectory;
        var sourceRemoteUri = context.State.SourceRemoteUri;
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDirectory)!);

        if (context.Settings.DryRun)
        {
            return $"Dry run: would clone or refresh '{context.Settings.SourceRepo}' from '{sourceRemoteUri}' into '{sourceDirectory}'.";
        }

        if (!Directory.Exists(sourceDirectory))
        {
            var cloneArguments = new List<string>
            {
                "clone",
                "--origin", "source",
                "--branch", context.Settings.SourceBranch,
            };

            if (LooksLikeLocalPath(context.Settings.SourceRepo))
                cloneArguments.Add("--no-hardlinks");

            cloneArguments.Add(sourceRemoteUri);
            cloneArguments.Add(sourceDirectory);

            context.Logger.Info($"Cloning '{sourceRemoteUri}' into '{sourceDirectory}'.");
            var cloneResult = await RunProcessAsync("git", cloneArguments, context.State.WorkDirectory).ConfigureAwait(false);
            EnsureCommandSucceeded(cloneResult, "git clone");
        }
        else
        {
            if (!IsGitRepository(sourceDirectory))
                throw new InvalidOperationException($"The clone directory '{sourceDirectory}' already exists but is not a git repository.");

            var remoteName = await GetPreferredRemoteNameAsync(sourceDirectory).ConfigureAwait(false);
            var remoteUrlResult = await RunProcessAsync("git", ["remote", "get-url", remoteName], sourceDirectory).ConfigureAwait(false);
            EnsureCommandSucceeded(remoteUrlResult, "git remote get-url");
            var actualRemoteUri = remoteUrlResult.Output.Trim();
            if (!RepositoryLocationsMatch(actualRemoteUri, sourceRemoteUri))
            {
                throw new InvalidOperationException(
                    $"The existing clone at '{sourceDirectory}' points at '{actualRemoteUri}', not '{sourceRemoteUri}'. " +
                    "Use --reset or a different --run-name to create a fresh working copy.");
            }

            context.Logger.Info($"Refreshing existing clone in '{sourceDirectory}' from remote '{remoteName}'.");

            var fetchResult = await RunProcessAsync("git", ["fetch", remoteName, "--prune", "--tags"], sourceDirectory).ConfigureAwait(false);
            EnsureCommandSucceeded(fetchResult, "git fetch");

            var checkoutResult = await RunProcessAsync(
                "git",
                ["checkout", "-B", context.Settings.SourceBranch, $"{remoteName}/{context.Settings.SourceBranch}"],
                sourceDirectory).ConfigureAwait(false);
            EnsureCommandSucceeded(checkoutResult, "git checkout");
        }

        var headCommitResult = await RunProcessAsync("git", ["rev-parse", "HEAD"], sourceDirectory).ConfigureAwait(false);
        EnsureCommandSucceeded(headCommitResult, "git rev-parse");
        context.State.SourceHeadCommit = headCommitResult.Output.Trim();

        return $"Cloned/refreshed '{context.Settings.SourceRepo}' into '{sourceDirectory}' at commit '{context.State.SourceHeadCommit}'.";
    }

    private static Task<string> TrimHistoryAsync(StageContext context)
    {
        var trimmedDirectory = context.State.TrimmedDirectory;
        Directory.CreateDirectory(trimmedDirectory);

        return Task.FromResult(
            "Scaffold placeholder only. A future milestone will trim the cloned repo history here " +
            "before the import step runs.");
    }

    private static Task<string> MergeIntoRoslynAsync(StageContext context)
    {
        var importDirectory = context.State.ImportPreviewDirectory;
        Directory.CreateDirectory(importDirectory);

        var fullTargetPath = GetAbsolutePath(context.RepoRoot, context.Settings.TargetPath);
        return Task.FromResult(
            $"Scaffold placeholder only. A future milestone will import the prepared repo into '{fullTargetPath}'.");
    }

    private static async Task<string> FinalizeScaffoldAsync(StageContext context)
    {
        var summary = new StringBuilder();
        summary.AppendLine("Repo merge scaffold summary");
        summary.AppendLine("=========================");
        summary.AppendLine($"Workflow version : {WorkflowVersion}");
        summary.AppendLine($"Run name         : {context.State.RunName}");
        summary.AppendLine($"Source repo      : {context.Settings.SourceRepo}");
        summary.AppendLine($"Source branch    : {context.Settings.SourceBranch}");
        summary.AppendLine($"Target path      : {context.Settings.TargetPath}");
        summary.AppendLine($"Work root        : {context.State.WorkRoot}");
        summary.AppendLine($"Clone directory  : {context.State.SourceCloneDirectory}");
        summary.AppendLine($"Source HEAD      : {context.State.SourceHeadCommit}");
        summary.AppendLine($"Dry run          : {context.Settings.DryRun}");
        summary.AppendLine($"State file       : {context.StatePath}");
        summary.AppendLine($"Log file         : {context.Logger.LogPath}");
        summary.AppendLine();
        summary.AppendLine("Available follow-up commands:");
        summary.AppendLine($@"  dotnet run --file eng\repo-merge.cs -- --run-name {context.State.RunName} --resume");
        summary.AppendLine($@"  dotnet run --file eng\repo-merge.cs -- --run-name {context.State.RunName} --stage clone-source --rerun");
        summary.AppendLine();
        summary.AppendLine("Current status: the external source-clone stage is implemented; trim/import are still placeholders.");

        var summaryPath = Path.Combine(context.RunDirectory, "summary.txt");
        await File.WriteAllTextAsync(summaryPath, summary.ToString()).ConfigureAwait(false);

        return $"Wrote scaffold summary to '{summaryPath}'.";
    }

    private static string GetRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (Path.GetDirectoryName(sourceFilePath) is not string engDirectory
            || Path.GetDirectoryName(engDirectory) is not string repoRoot
            || !File.Exists(Path.Combine(repoRoot, "eng", Path.GetFileName(sourceFilePath))))
        {
            throw new InvalidOperationException(
                "Could not determine the repo root. This script must live under the 'eng' directory of the roslyn repo.");
        }

        return repoRoot;
    }

    private static string GetDefaultRunName(string sourceRepo, string targetPath)
        => SanitizePathSegment($"{sourceRepo}-to-{targetPath}");

    private static string SanitizePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string GetAbsolutePath(string repoRoot, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repoRoot, path));

    private static void EnsurePathIsOutsideRepo(string repoRoot, string candidatePath, string optionName)
    {
        if (IsPathWithinRoot(repoRoot, candidatePath))
        {
            throw new InvalidOperationException(
                $"{optionName} resolved to '{candidatePath}', which is inside the roslyn repo. " +
                "Choose a path outside the checkout so folder structure and repo-local configuration cannot interfere.");
        }
    }

    private static bool IsPathWithinRoot(string rootPath, string candidatePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(normalizedCandidate),
                Path.TrimEndingDirectorySeparator(rootPath),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLocalPath(string value)
        => value.Contains('\\')
            || value.Contains(':')
            || value.StartsWith(".", StringComparison.Ordinal);

    private static string ResolveSourceRepositoryUri(string sourceRepo, string repoRoot)
        => LooksLikeLocalPath(sourceRepo)
            ? GetAbsolutePath(repoRoot, sourceRepo)
            : $"https://github.com/{sourceRepo}.git";

    private static bool IsGitRepository(string directory)
        => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    private static bool RepositoryLocationsMatch(string left, string right)
        => string.Equals(NormalizeRepositoryLocation(left), NormalizeRepositoryLocation(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRepositoryLocation(string value)
    {
        value = value.Trim().Replace('\\', '/');

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];

        return value.TrimEnd('/');
    }

    private static async Task<string> GetPreferredRemoteNameAsync(string repositoryDirectory)
    {
        var remoteList = await RunProcessAsync("git", ["remote"], repositoryDirectory).ConfigureAwait(false);
        EnsureCommandSucceeded(remoteList, "git remote");

        var remotes = remoteList.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (remotes.Contains("source"))
            return "source";

        if (remotes.Contains("origin"))
            return "origin";

        throw new InvalidOperationException(
            $"The repository at '{repositoryDirectory}' does not have a 'source' or 'origin' remote to refresh.");
    }

    private static void EnsureCommandSucceeded(ProcessResult result, string commandName)
    {
        if (result.ExitCode == 0)
            return;

        throw new InvalidOperationException(
            $"{commandName} failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output.Trim()}");
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        var output = (await standardOutputTask.ConfigureAwait(false)) + (await standardErrorTask.ConfigureAwait(false));
        return new ProcessResult(process.ExitCode, output);
    }
}

readonly record struct MergeSettings(
    string SourceRepo,
    string SourceBranch,
    string TargetPath,
    string StateRoot,
    string WorkRoot,
    string? RunName,
    string? Stage,
    string? StartAt,
    string? StopAfter,
    bool ListStages,
    bool DryRun,
    bool Resume,
    bool Rerun,
    bool Reset);

readonly record struct ExecutionPlan(int StartIndex, int StopIndex, string StartStageName, string StopStageName);

readonly record struct MergeStageDefinition(string Name, string Description, Func<StageContext, Task<string>> ExecuteAsync);

readonly record struct StageContext(
    MergeSettings Settings,
    string RepoRoot,
    string RunDirectory,
    string StatePath,
    MergeRunState State,
    RunLogger Logger);

readonly record struct ProcessResult(int ExitCode, string Output);

sealed class MergeRunState
{
    public int SchemaVersion { get; set; }
    public string WorkflowVersion { get; set; } = string.Empty;
    public string RunName { get; set; } = string.Empty;
    public string SourceRepo { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string StateRoot { get; set; } = string.Empty;
    public string WorkRoot { get; set; } = string.Empty;
    public string RunDirectory { get; set; } = string.Empty;
    public string WorkDirectory { get; set; } = string.Empty;
    public string RepoRoot { get; set; } = string.Empty;
    public string SourceRemoteUri { get; set; } = string.Empty;
    public string SourceCloneDirectory { get; set; } = string.Empty;
    public string TrimmedDirectory { get; set; } = string.Empty;
    public string ImportPreviewDirectory { get; set; } = string.Empty;
    public string SourceHeadCommit { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public string CurrentStage { get; set; } = string.Empty;
    public string LastCompletedStage { get; set; } = string.Empty;
    public string SelectedStartStage { get; set; } = string.Empty;
    public string SelectedStopStage { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<StageState> Stages { get; set; } = [];
}

sealed class StageState
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? LastMessage { get; set; }
}

enum StageStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
}

sealed class RunLogger
{
    public string LogPath { get; }

    public RunLogger(string logPath)
    {
        LogPath = logPath;
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] {level}: {message}";
        Console.WriteLine(line);
        File.AppendAllText(LogPath, line + Environment.NewLine);
    }
}
