using System.Globalization;

namespace Depkeeper.Cli;

/// <summary>
/// Coordinates discovery, bounded repairs, fresh CI checks, and exact-head merges.
/// </summary>
internal sealed class MaintenanceRunner
{
    private readonly IGitHubGateway _github;
    private readonly IRepairer _repairer;
    private readonly StateStore _state;
    private readonly Redactor _redactor;
    private readonly TimeProvider _clock;
    private readonly IReleaseAgeGate _releaseAge;
    private readonly TimeSpan _pollInterval;
    private readonly PostMergeVerifier _postMerge;
    private readonly FreshCi _freshCi;
    private readonly RecoveryRunner _recovery;
    private readonly ReviewCoordinator _reviews;
    private readonly NpmCompatibilityGate? _compatibility;

    /// <summary>
    /// Creates a controller with replaceable GitHub and repair boundaries.
    /// </summary>
    /// <param name="github">The GitHub gateway.</param>
    /// <param name="repairer">The repair implementation.</param>
    /// <param name="state">The durable checkpoint store.</param>
    /// <param name="redactor">The report redactor.</param>
    /// <param name="releaseAge">The publication-age gate.</param>
    /// <param name="clock">An optional test clock.</param>
    /// <param name="pollInterval">An optional polling interval for deterministic tests.</param>
    /// <param name="compatibility">An optional registry-backed npm peer compatibility gate.</param>
    internal MaintenanceRunner(IGitHubGateway github, IRepairer repairer, StateStore state, Redactor redactor,
        IReleaseAgeGate releaseAge, TimeProvider? clock = null, TimeSpan? pollInterval = null,
        NpmCompatibilityGate? compatibility = null)
    {
        _github = github;
        _repairer = repairer;
        _state = state;
        _redactor = redactor;
        _clock = clock ?? TimeProvider.System;
        _releaseAge = releaseAge;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
        _postMerge = new PostMergeVerifier(github, state, redactor, _clock, _pollInterval);
        _freshCi = new FreshCi(github, _clock, _pollInterval);
        _recovery = new RecoveryRunner(github, repairer, state, redactor, releaseAge, _postMerge, _freshCi, _clock, WaitAsync);
        _reviews = new ReviewCoordinator(github, redactor);
        _compatibility = compatibility;
    }

    /// <summary>
    /// Runs a maintenance sweep while containing failures to their repository or PR.
    /// </summary>
    /// <param name="settings">The run policy.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>Verified outcomes and unresolved blockers.</returns>
    internal async Task<IReadOnlyList<ReportEntry>> RunAsync(RunSettings settings, CancellationToken cancellationToken)
    {
        var results = new List<ReportEntry>();
        var repairs = 0;
        var startIndex = Math.Abs(_state.State.NextRepository % settings.Repositories.Length);
        for (var offset = 0; offset < settings.Repositories.Length; offset++)
        {
            var repositoryIndex = (startIndex + offset) % settings.Repositories.Length;
            var repository = settings.Repositories[repositoryIndex];
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var profile = settings.Profiles.GetValueOrDefault(repository) ?? new RepositoryProfile();
                var candidates = await _github.ListAsync(repository, cancellationToken);
                if (settings.OnlyPullRequest is int selected && candidates.All(pr => pr.Number != selected))
                {
                    var requested = await _github.RefreshAsync(PullRequestSnapshot.Lookup(repository, selected), cancellationToken);
                    if (requested.State == "MERGED" && requested.MergeCommit is not null &&
                        !_state.State.PullRequests.ContainsKey(requested.Key))
                    {
                        _state.State.PullRequests[requested.Key] = new AttemptState(requested.Head, 0, false,
                            "Verifying an explicitly selected merge.", _clock.GetUtcNow(), requested.MergeCommit, requested.BaseBranch);
                        if (!settings.DryRun) _state.Save();
                    }
                }
                var followups = (await _postMerge.RecheckAsync(repository, profile, settings.DryRun, settings.CiTimeout,
                    cancellationToken)).ToList();
                var mutated = false;
                if (!settings.DryRun && profile.AutoRecover)
                {
                    for (var index = 0; index < followups.Count; index++)
                    {
                        var entry = followups[index];
                        if (entry.Outcome != "blocked" || settings.OnlyPullRequest is int only && only != entry.Number) continue;
                        var key = repository + "#" + entry.Number;
                        if (!_state.State.PullRequests.TryGetValue(key, out var saved)) continue;
                        var (outcome, used, changed) = await _recovery.RunAsync(repository, entry.Number, saved, profile, settings,
                            settings.MaxRepairs - repairs, cancellationToken);
                        followups[index] = outcome;
                        repairs += used;
                        mutated |= changed;
                        if (used > 0) _state.SetNextRepository((repositoryIndex + 1) % settings.Repositories.Length);
                        if (changed) break;
                    }
                }
                results.AddRange(followups);
                var unresolvedMerge = followups.Any(entry => entry.Outcome != "merged");
                foreach (var candidate in candidates.Where(pr => settings.OnlyPullRequest is null || pr.Number == settings.OnlyPullRequest)
                    .OrderBy(pr => MergePolicy.HasFailure(pr, profile)))
                {
                    if (unresolvedMerge || mutated && !settings.DryRun)
                    {
                        results.Add(new ReportEntry(repository, candidate.Number, "deferred",
                            unresolvedMerge ? "Post-merge verification is unresolved for this repository." :
                                "Another PR in this repository was updated; reassess against the new base next run."));
                        continue;
                    }
                    var current = await _github.RefreshAsync(candidate, cancellationToken);
                    if (!MergePolicy.IsEligible(current)) continue;
                    var agePolicy = profile.ReleaseAge ?? settings.ReleaseAge ?? new ReleaseAgePolicy();
                    var reviewThreads = await _reviews.GetAsync(current, cancellationToken);
                    _state.State.PullRequests.TryGetValue(current.Key, out var earlier);
                    // GitHub recomputes mergeability lazily after the base moves; a blocked verdict is not reaffirmed on a stale value.
                    if (earlier is { Blocked: true }) current = await WaitForMergeabilityAsync(current, cancellationToken);
                    // A head that Dependabot produced at the controller's request inherits the earlier repair budget and verdict.
                    var rebased = earlier is { RebaseRequested: true } && earlier.Head != current.Head;
                    var rebasedBlocked = rebased && earlier!.Blocked;
                    var previous = earlier?.Head == current.Head ? earlier : null;
                    // The PR's recorded base is only refreshed when its head moves, so the live branch tip tells whether the base moved.
                    var baseTip = previous?.Blocked == true || NeedsRebase(current)
                        ? await _github.GetBranchHeadAsync(repository, current.BaseBranch, cancellationToken) : null;
                    var baseMoved = previous?.Blocked == true && previous.BaseHead != baseTip && NeedsRebase(current);
                    var validationFeedback = earlier is null ? null : ValidationFeedback(earlier);
                    var resumeValidation = validationFeedback is not null && earlier!.Attempts < settings.MaxAttempts &&
                        (previous is not null || rebasedBlocked);
                    var repairsAllowed = settings.RetryBlocked || resumeValidation || !rebasedBlocked && !baseMoved;
                    var attempts = settings.RetryBlocked ? 0 : previous?.Attempts ?? (rebased ? earlier!.Attempts : 0);
                    var repairFeedback = resumeValidation ? validationFeedback :
                        previous?.RepairFailure ?? (rebasedBlocked ? earlier!.RepairFailure : null);
                    var retriedHead = previous?.CiRetriedHead;
                    try
                    {
                        NpmCompatibilityAssessment? compatibility = null;
                        string? inspectedHead = null;
                        BranchOwnership? conflictOwnership = null;
                        if (previous?.Blocked == true && current.MergeState == "DIRTY" && reviewThreads.Count == 0 &&
                            MergePolicy.ChecksFinished(current) && !MergePolicy.HasFailure(current, profile))
                        {
                            // A repair that passed CI but no longer merges cleanly is not a repair verdict; recreating the
                            // update discards it so the current base can be verified within the remaining repair budget.
                            conflictOwnership = await _github.GetBranchOwnershipAsync(current, cancellationToken);
                            if (conflictOwnership == BranchOwnership.DependabotWithRepairs) previous = previous with { Blocked = false };
                        }
                        if (previous?.Blocked == true && !settings.RetryBlocked && !baseMoved &&
                            (MergePolicy.GetBlocker(current, current.Head, profile) is not null || reviewThreads.Count > 0))
                        {
                            compatibility = await CheckCompatibilityAsync(current, profile, cancellationToken);
                            inspectedHead = current.Head;
                            if (previous.UpstreamConflict is not null && compatibility?.CompatibleVersion is not null &&
                                previous.UpstreamConflict == compatibility.Conflict)
                            {
                                attempts = 0;
                                repairFeedback = null;
                                repairsAllowed = true;
                                previous = previous with { Blocked = false, Attempts = 0, RepairFailure = null, UpstreamConflict = null };
                                if (!settings.DryRun) _state.Set(current.Key, previous);
                            }
                            else if (previous.UpstreamConflict is null && resumeValidation &&
                                compatibility is not { CompatibleVersion: null })
                            {
                                previous = previous with { Blocked = false, RepairFailure = repairFeedback };
                                if (!settings.DryRun) _state.Set(current.Key, previous);
                            }
                            else
                            {
                                // A verdict reached without rerunning the failure may rest on a transient CI fault.
                                if (!settings.DryRun && compatibility is not { CompatibleVersion: null } &&
                                    NeedsRetry(current, profile, previous.CiRetriedHead, reviewThreads.Count))
                                {
                                    retriedHead = current.Head;
                                    previous = previous with { CiRetriedHead = retriedHead };
                                    _state.Set(current.Key, previous);
                                    var (retried, requested, finished) = await _freshCi.RetryFailuresAsync(current, profile,
                                        settings.CiTimeout, cancellationToken);
                                    if (retried.Head != current.Head || requested && !finished)
                                    {
                                        results.Add(new ReportEntry(repository, current.Number, "pending",
                                            "Waiting for the rerun of failed checks."));
                                        continue;
                                    }
                                    current = retried;
                                    if (!MergePolicy.HasFailure(current, profile)) previous = previous with { Blocked = false };
                                }
                                if (previous.Blocked)
                                {
                                    var history = previous.RepairFailure ?? previous.Reason;
                                    var conflict = compatibility is { CompatibleVersion: null } ? compatibility.Conflict :
                                        previous.UpstreamConflict;
                                    results.Add(RecordBlocker(current, profile, previous with { RepairFailure = history }, conflict,
                                        reviewThreads.Count, settings.DryRun, compatibility is { CompatibleVersion: null },
                                        CanWaitUpstream(previous)));
                                    continue;
                                }
                            }
                        }
                        var ownership = NeedsRebase(current)
                            ? conflictOwnership ?? await _github.GetBranchOwnershipAsync(current, cancellationToken)
                            : BranchOwnership.Foreign;
                        var owned = ownership is BranchOwnership.Dependabot or BranchOwnership.DependabotWithMerges;
                        if (ownership != BranchOwnership.Foreign || current.MergeState == "BEHIND")
                        {
                            if (settings.DryRun)
                            {
                                results.Add(new ReportEntry(repository, current.Number, "would-update",
                                    (baseMoved ? "The base changed since this revision was blocked. " : string.Empty) +
                                    (owned ? "Ask Dependabot to rebase and rerun CI." : "Bring the branch up to date and rerun CI.")));
                                continue;
                            }
                            var updated = await BringUpToDateAsync(current, ownership, previous, baseTip!, attempts,
                                previous?.Blocked ?? false, settings.CiTimeout, cancellationToken);
                            mutated = true;
                            if (updated is null)
                            {
                                results.Add(new ReportEntry(repository, current.Number, "pending",
                                    owned ? "Waiting for Dependabot to rebase the branch." : "Waiting for the updated branch."));
                                continue;
                            }
                            current = updated;
                            reviewThreads = await _reviews.GetAsync(current, cancellationToken);
                        }
                        var ageBlocker = await CheckReleaseAgeAsync(current, agePolicy, cancellationToken);
                        if (ageBlocker is not null)
                        {
                            results.Add(ageBlocker);
                            continue;
                        }
                        if (inspectedHead != current.Head)
                            compatibility = await CheckCompatibilityAsync(current, profile, cancellationToken);
                        if (compatibility is { CompatibleVersion: null })
                        {
                            var saved = new AttemptState(current.Head, attempts, true, "", _clock.GetUtcNow(),
                                BaseHead: await BaseTipAsync(repository, current, cancellationToken), RepairFailure: repairFeedback);
                            results.Add(RecordBlocker(current, profile, saved, compatibility.Conflict,
                                reviewThreads.Count, settings.DryRun,
                                resumeUpstream: repairsAllowed || earlier is null || CanWaitUpstream(earlier)));
                            continue;
                        }
                        if (settings.DryRun)
                        {
                            var blocker = MergePolicy.GetBlocker(current, current.Head, profile);
                            var outcome = _freshCi.Needed(current, profile) ? "would-refresh" :
                                reviewThreads.Count > 0 || MergePolicy.HasFailure(current, profile) ? "would-repair" :
                                blocker is null ? "would-merge" :
                                MergePolicy.IsPending(current) ? "pending" : "blocked";
                            results.Add(new ReportEntry(repository, current.Number,
                                outcome, outcome == "would-refresh" ? "Successful CI is older than the configured freshness window." :
                                    reviewThreads.Count > 0 ? $"{reviewThreads.Count} unresolved review thread(s)." :
                                    blocker ?? "All observed merge gates pass."));
                            continue;
                        }

                        if (MergePolicy.ChecksFinished(current) && _freshCi.Needed(current, profile))
                        {
                            var expected = current.Head;
                            mutated = true;
                            current = await _freshCi.EnsureAsync(current, profile, settings.CiTimeout, cancellationToken);
                            if (current.Head != expected)
                            {
                                results.Add(new ReportEntry(repository, current.Number, "pending", "The PR changed during CI refresh."));
                                continue;
                            }
                        }
                        if (!MergePolicy.ChecksFinished(current))
                        {
                            results.Add(new ReportEntry(repository, current.Number, "pending", "CI is still running for this revision."));
                            continue;
                        }
                        if (MergePolicy.HasFailure(current, profile) && repairsAllowed && attempts < settings.MaxAttempts &&
                            repairs < settings.MaxRepairs)
                        {
                            // Branch rules need not require up-to-date branches, so GitHub may not report a stale head as behind.
                            // A repair is made against the current base, which may already resolve the failure or conflict with it.
                            var liveBase = await _github.GetBranchHeadAsync(repository, current.BaseBranch, cancellationToken);
                            var staleOwnership = await _github.IsBehindAsync(current, liveBase, cancellationToken)
                                ? await _github.GetBranchOwnershipAsync(current, cancellationToken) : BranchOwnership.Foreign;
                            if (staleOwnership != BranchOwnership.Foreign)
                            {
                                var updated = await BringUpToDateAsync(current, staleOwnership, previous, liveBase, attempts, false,
                                    settings.CiTimeout, cancellationToken);
                                mutated = true;
                                if (updated is null)
                                {
                                    results.Add(new ReportEntry(repository, current.Number, "pending",
                                        "Waiting for the branch to include its current base before repairing."));
                                    continue;
                                }
                                current = updated;
                                reviewThreads = await _reviews.GetAsync(current, cancellationToken);
                                if (!MergePolicy.ChecksFinished(current))
                                {
                                    results.Add(new ReportEntry(repository, current.Number, "pending",
                                        "CI is still running for this revision."));
                                    continue;
                                }
                            }
                        }
                        if (NeedsRetry(current, profile, retriedHead, reviewThreads.Count))
                        {
                            // A failed job is rerun once before a repair, so a transient fault does not spend a repair.
                            retriedHead = current.Head;
                            _state.Set(current.Key, (previous?.Head == current.Head ? previous :
                                new AttemptState(current.Head, attempts, false, "", _clock.GetUtcNow(), RepairFailure: repairFeedback)) with
                            {
                                Reason = "Waiting for the rerun of failed checks.",
                                UpdatedAt = _clock.GetUtcNow(),
                                CiRetriedHead = retriedHead
                            });
                            var (retried, requested, finished) = await _freshCi.RetryFailuresAsync(current, profile,
                                settings.CiTimeout, cancellationToken);
                            if (retried.Head != current.Head || requested && !finished)
                            {
                                results.Add(new ReportEntry(repository, current.Number, "pending",
                                    "Waiting for the rerun of failed checks."));
                                continue;
                            }
                            current = retried;
                        }

                        while ((MergePolicy.HasFailure(current, profile) || reviewThreads.Count > 0) && repairsAllowed &&
                            attempts < settings.MaxAttempts && repairs < settings.MaxRepairs)
                        {
                            attempts++;
                            repairs++;
                            mutated = true;
                            _state.SetNextRepository((repositoryIndex + 1) % settings.Repositories.Length);
                            _state.Set(current.Key,
                                new AttemptState(current.Head, attempts, false, "Repair in progress.", _clock.GetUtcNow(),
                                    RepairFailure: repairFeedback, CiRetriedHead: retriedHead));
                            var currentFailures = await _github.GetFailureLogsAsync(current, cancellationToken);
                            var context = _reviews.Format(reviewThreads);
                            if (settings.RetryBlocked && previous?.Blocked == true)
                                context += "\nPrevious independent verification for this revision:\n" +
                                    (previous.RepairFailure ?? previous.Reason);
                            if (!string.IsNullOrWhiteSpace(repairFeedback))
                                context += "\nIndependent validation rejected the previous candidate:\n" + repairFeedback;
                            if (compatibility?.CompatibleVersion is string available)
                                context += $"\nRegistry metadata identifies {compatibility.Conflict.Package}@{available} " +
                                    "as supporting the selected peer version. Preserve all dependency values introduced by this PR.";
                            var evidence = new RepairEvidence(currentFailures, context);
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeout.CancelAfter(settings.RepairTimeout);
                            RepairResult repair;
                            try { repair = await _repairer.RepairAsync(current, evidence, profile, timeout.Token); }
                            catch (RepairValidationException exception)
                            {
                                repairFeedback = _redactor.Clean(exception.Message);
                                if (repairFeedback.Length > 1200) repairFeedback = repairFeedback[..1200];
                                _state.Set(current.Key, new AttemptState(current.Head, attempts, false, repairFeedback,
                                    _clock.GetUtcNow(), RepairFailure: repairFeedback, CiRetriedHead: retriedHead));
                                continue;
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                throw new InvalidOperationException(
                                    "Repair exceeded its time budget. Inspect the failure and retry explicitly.");
                            }
                            if (repair.Head is null) throw new InvalidOperationException(repair.Summary);
                            repairFeedback = null;
                            mutated = true;
                            current = current with { Head = repair.Head };
                            _state.Set(current.Key, new AttemptState(current.Head, attempts, false, "Waiting for CI.", _clock.GetUtcNow()));
                            current = await WaitAsync(current, settings.CiTimeout, false, cancellationToken);
                            if (current.Head != repair.Head)
                                throw new InvalidOperationException(
                                    "The PR changed after repair; inspect the new revision before continuing.");
                            reviewThreads = await _reviews.ResolveAddressedAsync(current, reviewThreads,
                                repair.ChangedPaths ?? [], cancellationToken);
                        }

                        if (MergePolicy.HasFailure(current, profile) || reviewThreads.Count > 0)
                        {
                            if (!repairsAllowed)
                                throw new InvalidOperationException(
                                    "Checks still fail after the branch was brought up to date.");
                            if (attempts >= settings.MaxAttempts)
                                throw new InvalidOperationException(
                                    "Repair attempt limit reached. " + (repairFeedback is null ? "" : repairFeedback + " ") +
                                    "Remaining checks or review threads: " +
                                    FailedNames(current, reviewThreads));
                            results.Add(new ReportEntry(repository, current.Number, "deferred",
                                "Daily repair budget reached. Remaining checks or review threads: " +
                                FailedNames(current, reviewThreads)));
                            continue;
                        }
                        var verifiedHead = current.Head;
                        current = await _github.RefreshAsync(current, cancellationToken);
                        if (current.Head == verifiedHead && NeedsRebase(current))
                        {
                            var finalOwnership = await _github.GetBranchOwnershipAsync(current, cancellationToken);
                            var liveBase = await _github.GetBranchHeadAsync(repository, current.BaseBranch, cancellationToken);
                            // A repair pushed during this run has its own checkpoint; the earlier revision's state no longer applies.
                            var verified = _state.State.PullRequests.GetValueOrDefault(current.Key) is { } saved &&
                                saved.Head == current.Head ? saved : null;
                            var updated = await BringUpToDateAsync(current, finalOwnership, verified, liveBase, attempts,
                                verified?.Blocked ?? false, settings.CiTimeout, cancellationToken);
                            mutated = true;
                            if (updated is null)
                            {
                                results.Add(new ReportEntry(repository, current.Number, "pending",
                                    finalOwnership is BranchOwnership.Foreign or BranchOwnership.DependabotWithRepairs
                                        ? "Waiting for the updated branch." : "Waiting for Dependabot to rebase the branch."));
                                continue;
                            }
                            current = updated;
                            verifiedHead = current.Head;
                            if (!MergePolicy.ChecksFinished(current) || MergePolicy.HasFailure(current, profile))
                            {
                                results.Add(new ReportEntry(repository, current.Number, "pending",
                                    "The branch was updated after final verification; reassess its CI next run."));
                                continue;
                            }
                        }
                        reviewThreads = await _reviews.GetAsync(current, cancellationToken);
                        if (reviewThreads.Count > 0)
                        {
                            results.Add(new ReportEntry(repository, current.Number, "blocked",
                                "Unresolved review feedback remains on the exact PR revision."));
                            continue;
                        }
                        var finalAgeBlocker = await CheckReleaseAgeAsync(current, agePolicy, cancellationToken);
                        if (finalAgeBlocker is not null)
                        {
                            results.Add(finalAgeBlocker);
                            continue;
                        }
                        var reason = MergePolicy.GetBlocker(current, verifiedHead, profile);
                        if (reason is not null)
                        {
                            results.Add(new ReportEntry(repository, current.Number,
                                MergePolicy.IsPending(current) ? "pending" : "blocked", reason));
                            continue;
                        }
                        var mergeHead = await _github.MergeAsync(current, cancellationToken) ??
                            throw new InvalidOperationException(
                                "GitHub did not confirm a merge; inspect branch rules, merge queues, and review threads.");
                        mutated = true;
                        var mergedAttempt = new AttemptState(current.Head, attempts, false, "Waiting for post-merge CI.",
                            _clock.GetUtcNow(), mergeHead, current.BaseBranch);
                        _state.Set(current.Key, mergedAttempt);
                        results.Add(await _postMerge.VerifyAsync(repository, current.Number, mergedAttempt, profile, false,
                            settings.CiTimeout, cancellationToken));
                        var advisory = current.Checks.Where(check =>
                            (profile.AdvisoryChecks ?? []).Contains(check.Name, StringComparer.Ordinal) &&
                            check.State is not ("SUCCESS" or "NEUTRAL" or "SKIPPED")).Select(check => check.Name).ToArray();
                        if (advisory.Length > 0) results.Add(new ReportEntry(repository, current.Number, "advisory",
                            "Nonblocking checks: " + string.Join(", ", advisory)));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (TimeoutException) { throw; }
                    catch (Exception exception) when (FailurePolicy.CanReport(exception))
                    {
                        var reason = _redactor.Clean(exception.Message);
                        if (reason.Length > 1500) reason = reason[..1500];
                        var history = repairFeedback ?? (rebasedBlocked || baseMoved ?
                            earlier?.RepairFailure ?? earlier?.Reason : reason);
                        var detail = BlockerReport.Format(current, profile, history, reviewThreads.Count, _redactor);
                        results.Add(new ReportEntry(repository, current.Number, "blocked", detail));
                        if (!settings.DryRun)
                        {
                            _state.Set(current.Key, new AttemptState(current.Head, attempts, true, detail, _clock.GetUtcNow(),
                                BaseHead: await BaseTipAsync(repository, current, cancellationToken), RepairFailure: history,
                                CiRetriedHead: retriedHead));
                            try { await _github.CommentAsync(current, "Depkeeper stopped this repair: " + detail, cancellationToken); }
                            catch (Exception commentError) when (commentError is IOException or InvalidOperationException)
                            {
                                results.Add(new ReportEntry(repository, current.Number, "advisory",
                                    "The PR comment could not be posted; the blocker is recorded in this report."));
                            }
                        }
                    }
                }
                results.AddRange(await RetireClosedAsync(repository, candidates, settings, cancellationToken));
                if (candidates.Count == 0 && followups.Count == 0)
                    results.Add(new ReportEntry(repository, 0, "clear", "No open Dependabot PRs."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (TimeoutException) { throw; }
            catch (Exception exception) when (FailurePolicy.CanReport(exception))
            {
                results.Add(new ReportEntry(repository, 0, "blocked", _redactor.Clean(exception.Message)));
            }
        }
        if (!settings.DryRun) _state.Save();
        return results;
    }

    private static bool NeedsRebase(PullRequestSnapshot pullRequest) => pullRequest.MergeState is "BEHIND" or "DIRTY";

    private static bool NeedsRetry(PullRequestSnapshot pullRequest, RepositoryProfile profile, string? retriedHead, int reviews) =>
        reviews == 0 && retriedHead != pullRequest.Head && MergePolicy.ChecksFinished(pullRequest) &&
        MergePolicy.HasFailure(pullRequest, profile);

    private async Task<IReadOnlyList<ReportEntry>> RetireClosedAsync(string repository, IReadOnlyList<PullRequestSnapshot> candidates,
        RunSettings settings, CancellationToken cancellationToken)
    {
        // Dependabot closes a superseded PR instead of updating it, so a verdict recorded for it must not keep its issue open.
        var results = new List<ReportEntry>();
        var prefix = repository + "#";
        var tracked = _state.State.PullRequests
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value.MergeHead is null)
            .Select(pair => (pair.Key, Number: int.TryParse(pair.Key.AsSpan(prefix.Length), CultureInfo.InvariantCulture, out var number)
                ? number : 0))
            .Where(entry => entry.Number > 0 && candidates.All(pr => pr.Number != entry.Number) &&
                (settings.OnlyPullRequest is not int only || only == entry.Number))
            .ToArray();
        foreach (var (key, number) in tracked)
        {
            PullRequestSnapshot current;
            try { current = await _github.RefreshAsync(PullRequestSnapshot.Lookup(repository, number), cancellationToken); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException) { continue; }
            if (current.State is not ("CLOSED" or "MERGED")) continue;
            results.Add(new ReportEntry(repository, number, "closed", current.State == "MERGED"
                ? "The PR was merged outside Depkeeper; its earlier blocker no longer applies."
                : "The PR was closed without merging; its earlier blocker no longer applies."));
            if (settings.DryRun) continue;
            _state.State.PullRequests.Remove(key);
            _state.Save();
        }
        return results;
    }

    private async Task<ReportEntry?> CheckReleaseAgeAsync(PullRequestSnapshot current, ReleaseAgePolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            var blocker = await _releaseAge.GetBlockerAsync(current, policy, cancellationToken);
            return blocker is null ? null : new ReportEntry(current.Repository, current.Number, "cooldown", blocker);
        }
        catch (DependencyReviewUnavailableException exception)
        {
            // Waiting never restores dependency review, so this is reported for attention rather than as a cooldown.
            return new ReportEntry(current.Repository, current.Number, "blocked", exception.Message);
        }
    }

    private async Task<string?> BaseTipAsync(string repository, PullRequestSnapshot current, CancellationToken cancellationToken)
    {
        try { return await _github.GetBranchHeadAsync(repository, current.BaseBranch, cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { return null; }
    }

    private async Task<NpmCompatibilityAssessment?> CheckCompatibilityAsync(PullRequestSnapshot current, RepositoryProfile profile,
        CancellationToken cancellationToken) => _compatibility is not null && MergePolicy.ChecksFinished(current) &&
        MergePolicy.HasFailure(current, profile) ? await _compatibility.CheckAsync(current,
            await _github.GetFailureLogsAsync(current, cancellationToken), cancellationToken) : null;

    private ReportEntry RecordBlocker(PullRequestSnapshot current, RepositoryProfile profile, AttemptState saved,
        NpmPeerConflict? conflict, int reviews, bool dryRun, bool upstreamVerified = true, bool resumeUpstream = true)
    {
        var upstream = conflict is null ? null : upstreamVerified ? conflict.Summary :
            "Upstream compatibility could not be refreshed. Last verified blocker: " + conflict.Summary;
        var detail = BlockerReport.Format(current, profile, saved.RepairFailure, reviews, _redactor,
            upstream);
        if (!dryRun)
            _state.Set(current.Key, saved with
            {
                Head = current.Head,
                Blocked = true,
                Reason = detail,
                RepairFailure = saved.RepairFailure ?? "",
                UpstreamConflict = resumeUpstream ? conflict : null
            });
        // A registry-verified upstream wait resumes automatically and offers the developer nothing to do.
        return new ReportEntry(current.Repository, current.Number, conflict is null ? "blocked" : "upstream", detail);
    }

    private static bool CanWaitUpstream(AttemptState saved)
    {
        return saved.UpstreamConflict is not null || ValidationFeedback(saved) is not null;
    }

    private static string? ValidationFeedback(AttemptState saved)
    {
        var failure = saved.RepairFailure ?? saved.Reason;
        const string legacy = "Checks still fail after the branch was brought up to date; the earlier blocker stands: ";
        while (failure.StartsWith(legacy, StringComparison.Ordinal)) failure = failure[legacy.Length..];
        return failure.StartsWith("Independent validation failed:", StringComparison.Ordinal) ||
            failure.StartsWith("Repair reverted the Dependabot dependency update", StringComparison.Ordinal) ? failure : null;
    }

    private async Task<PullRequestSnapshot> WaitForMergeabilityAsync(PullRequestSnapshot current, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromMinutes(2);
        while ((current.Mergeable == "UNKNOWN" || current.MergeState == "UNKNOWN") && _clock.GetUtcNow() < deadline)
        {
            await Task.Delay(_pollInterval, _clock, cancellationToken);
            current = await _github.RefreshAsync(current, cancellationToken);
        }
        return current;
    }

    private async Task<PullRequestSnapshot?> BringUpToDateAsync(PullRequestSnapshot current, BranchOwnership ownership,
        AttemptState? previous, string baseTip, int attempts, bool blocked, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requested = previous is { RebaseRequested: true } && previous.BaseHead == baseTip;
        // A merge from the base keeps a verified repair, so it is tried before the repair is discarded.
        var merge = ownership == BranchOwnership.Foreign || ownership == BranchOwnership.DependabotWithRepairs && !requested;
        if (!merge || !await _github.UpdateBranchAsync(current, cancellationToken))
        {
            if (ownership == BranchOwnership.Foreign)
                throw new InvalidOperationException("GitHub could not update the branch; resolve conflicts or permissions.");
            if (requested)
            {
                if (previous!.UpdatedAt + TimeSpan.FromDays(1) < _clock.GetUtcNow())
                    throw new InvalidOperationException(
                        $"Dependabot has not rebased this branch since {previous.UpdatedAt:u}; rebase or close the PR manually.");
            }
            else
            {
                // Dependabot stops rebasing a branch once another account pushes to it, so the controller asks instead of merging.
                // It refuses to rebase over a merge from the base or a conflicting controller repair and must recreate the update.
                await _github.CommentAsync(current,
                    ownership == BranchOwnership.Dependabot ? "@dependabot rebase" : "@dependabot recreate", cancellationToken);
                _state.Set(current.Key, new AttemptState(current.Head, attempts, blocked,
                    previous?.Reason ?? "Waiting for Dependabot to rebase.", _clock.GetUtcNow(), BaseHead: baseTip,
                    RebaseRequested: true, RepairFailure: previous?.RepairFailure, UpstreamConflict: previous?.UpstreamConflict));
            }
        }
        var latest = await WaitAsync(current, timeout, true, cancellationToken);
        return latest.Head == current.Head ? null : latest;
    }

    private async Task<PullRequestSnapshot> WaitAsync(PullRequestSnapshot expected, TimeSpan timeout, bool requireNewHead,
        CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + timeout;
        var latest = expected;
        while (_clock.GetUtcNow() < deadline)
        {
            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _clock, cancellationToken);
            latest = await _github.RefreshAsync(expected, cancellationToken);
            if (requireNewHead && latest.Head == expected.Head) continue;
            if (!requireNewHead && latest.Head != expected.Head) return latest;
            if (MergePolicy.ChecksFinished(latest) && latest.Mergeable != "UNKNOWN") return latest;
        }
        return latest;
    }

    private static string FailedNames(PullRequestSnapshot pullRequest, IReadOnlyList<ReviewThread>? reviews = null)
    {
        var names = pullRequest.Checks
            .Where(check => check.State is "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED")
            .Select(check => check.Name).ToList();
        if (reviews?.Count > 0) names.Add($"{reviews.Count} unresolved review thread(s)");
        return string.Join(", ", names);
    }
}
