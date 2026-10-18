# Operation

The controller uses `gh` for GitHub operations and the Copilot SDK for repairs.
The model can inspect and edit the checkout. Its shell tool runs in an isolated
Docker container with read-only Git metadata and no forwarded host credentials.
The controller performs the Git operations, scans changes, and makes merge decisions.

Before pushing, Depkeeper reruns the original verification commands and scans the
candidate with Picket. It refuses deleted files, symbolic-link escapes, security/CI
configuration changes, and changes to npm scripts, identity, version, or engines.
Those cases are reported for manual review rather than bypassing checks.
It also rejects repairs that revert a direct dependency value introduced by the
Dependabot head.
Independent validation failures and rejected reversions feed the next controller-owned
attempt with the original CI evidence kept separate. Each attempt uses a fresh checkout,
the configured attempt and daily repair budgets still apply, and exhausted heads remain
blocked. Protected-path, security-policy, infrastructure, and timeout failures stop the
repair immediately. Validation feedback survives a daily-budget deferral.
Older checkpoints stopped by a recoverable validation failure may spend their remaining
attempts without resetting the consumed budget, including after a controller-requested rebase.
Generated `LICENSES/*.txt` entries may be removed when the original npm license check
is selected and passes independent verification. Node projects use their full `verify`
script when available.
An existing npm `allowScripts` permission may follow an exact dependency version update
only when its value is unchanged and no additional package receives permission.

A Dependabot branch that is behind or conflicts with its base is brought up to date by
asking Dependabot to rebase it, or to recreate it when the branch carries merges from the
base. Depkeeper does not push merge commits onto Dependabot branches, because Dependabot
stops rebasing a branch once another account pushes to it. Branches that already carry a
Depkeeper repair are updated with a merge from the base.

Before merging, it refreshes the PR, checks the exact revision, reevaluates publication
age, and lets GitHub enforce branch rules. Missing, pending, failed, or unexpectedly
skipped required checks prevent merging. Explicitly configured advisory failures are
reported. Green GitHub Actions checks older than the configured freshness window are
rerun before merging. This catches advisories published after an earlier audit passed.
The bot does not create release tags.

After merging, it records the resulting commit and verifies that commit's checks,
statuses, and complete push workflows. A successful PR check does not substitute for
post-merge CI. `requiredChecks` also applies after merging unless `postMergeChecks`
selects a different set of mandatory contexts for the base branch.
Unfinished verification is retained across sweeps, and post-merge failures pause further
updates in that repository. A later base commit can resolve the blocker after both its
ancestry and its own CI are verified.

Before each repair and immediately before merging, Depkeeper retrieves unresolved inline
review threads. Review text is untrusted input. New feedback after a push enters the next
bounded repair attempt. A thread is resolved only when its latest comment is unchanged,
the referenced file changed, and the candidate passed independent validation.

Persistent post-merge failures receive one CI retry. If the same commit still fails,
the controller may create one `depkeeper/repair-*` branch and ask Copilot for a focused
fix. It validates and scans the candidate before pushing, creates an assigned and labeled
recovery PR, verifies its exact head, merges without bypasses, and verifies the resulting
base commit. Recovery state and attempts persist across runs, so duplicate PRs are not
created. Changes that may alter dependencies remain subject to publication-age policy.

## Reports and recovery

- `.state/state.json` checkpoints attempts before repair starts, after candidate pushes,
  and while verifying merged commits.
- The workflow restores the latest retained checkpoint and uploads it even after failures.
- `.state/report.md` and the Actions summary show every outcome.
- Actionable blockers open or update a managed issue in the affected repository. Successful
  post-merge verification closes the corresponding managed issue, and so does a tracked PR that
  is closed or merged without Depkeeper. Pending CI and cooldowns do not open issues.
  `--report-repo OWNER/REPO` explicitly selects a central destination instead.
- An exhausted or terminal blocked revision is not automatically attempted again. A new head or an
  explicit `--retry-blocked` permits reconsideration. When the base moves, a blocked
  Dependabot branch is rebased and its CI rechecked without a new repair.
- Blocked reports describe the exact current head's checks and identify earlier repair
  diagnostics as history. A rebase does not turn an old validation failure into current CI evidence.
- For npm ERESOLVE failures, the controller can recognize incompatible exact direct
  dependencies in changed package manifests when the requiring package was not itself
  updated by the PR. It verifies both declarations at the PR head, the requiring package's
  unchanged value at the exact base, and its peer metadata in the public npm registry. When no
  nondeprecated stable release at or above the selected version supports the peer,
  the update waits upstream without spending model attempts. Later sweeps inspect registry
  metadata again; a compatible stable release reopens that upstream blocker with a fresh
  bounded repair budget. Registry outages do not reopen it. Ranges, aliases, transitive-only
  conflicts, and incomplete evidence retain the ordinary repair policy. Depkeeper does not
  force peer resolution, revert the requested update, or split grouped PRs.
  Unrelated terminal security and infrastructure failures retain their blocked verdict.
- Exit `0` means the sweep completed; `2` means it reported blockers; `1` means a run-level
  failure. State and report artifacts remain available for inspection.

The workflow wrapper treats controller exit `2` as a completed run because actionable
blockers are persisted and reported. Exit `1` and cancellation still fail the workflow.

The workflow serializes sweeps. Checkpoints are retained for 90 days. After a longer
inactivity period, no retained checkpoint means a fresh attempt history.

## Verification

Unit tests cover merge gates, stale revisions, blocked-state persistence, cooldowns,
lock-file comparison, registry verification, toolchain detection, redaction, and workspace
containment. Normal CI uses no model credits.

Run the **Live repair smoke** workflow, or run locally:

```sh
dotnet run --project src/Depkeeper.Cli -- repair-smoke --model gpt-5.6-luna
```

This uses a real Copilot session on a disposable broken fixture, requires the original
test to remain unchanged, and independently verifies the repaired code. It makes no
GitHub repository changes.
