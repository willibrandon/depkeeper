# Configuration

Set deployment variables with `gh variable set NAME --body VALUE`:

| Variable | Value |
| --- | --- |
| `DEPKEEPER_MODEL` | Copilot model ID; default `auto` |
| `DEPKEEPER_REPOSITORIES` | JSON array of `OWNER/REPO` names |
| `DEPKEEPER_PROFILES` | JSON object of trusted repository profiles |
| `DEPKEEPER_MIN_RELEASE_AGE_DAYS` | Ordinary-update cooldown; default `3` |
| `DEPKEEPER_MAX_REPAIRS` | Maximum repair sessions per daily sweep; default `3` |

The SDK's `models` command lists model IDs available to your account.

## Repository profiles

An optional `depkeeper.json` configures repository-specific environments and checks.
See [the example](../examples/depkeeper.json). CLI selections override repository/model
defaults; Actions variables override the file's repository/model defaults.

Profiles can supply `image`, `prepare`, `install`, `verify`, `requiredChecks`, `postMergeChecks`, `advisoryChecks`,
`maximumCheckAgeHours`, `autoRecover`, `recoveryAssignee`, and `releaseAge`. Explicit image and command overrides
take precedence over detection. Successful GitHub Actions checks older than one hour are rerun by default before merge.
Set `maximumCheckAgeHours` to zero to disable that freshness gate.
`prepare` adds deployment-controlled prerequisites before the detected package installation
without replacing it. Environment profiles replace profiles from `depkeeper.json`.
Commands use the image's POSIX shell and PATH. Exact Node engine versions and npm
`packageManager` declarations are honored; other version ranges use the default image.
Detected Node images include controller-installed CMake, Ninja, and pkg-config for
native addon and parser builds, plus distribution Rust tools when a root Cargo manifest
is present. Explicit image overrides supply their own prerequisites.

| Detected manifests | Default validation |
| --- | --- |
| `package.json` | npm/pnpm/Yarn installation and available quality/test scripts |
| `.sln`, `.slnx`, `.csproj`, `.fsproj`, `.vbproj` | .NET restore, build, and tests; MTP recognized from `global.json` |
| `Cargo.toml` | Cargo fetch and all-target tests |
| `go.mod` | Module download, tests, and vet |
| `pyproject.toml`, `requirements.txt` | Virtual environment and pytest |
| `pom.xml`, `gradlew` | Maven verify or Gradle check |
| `Package.swift`, `composer.json`, `Gemfile` | Swift, Composer, or Bundler test commands |

C/C++, Zig, platform-specific projects, and unconventional layouts use explicit
profiles with a suitable image and commands. Full GitHub CI remains the final gate,
including Windows/macOS, binding matrices, and remote-host checks.

## Release age

The default is **three days**, consistent with
[Dependabot's default cooldown](https://docs.github.com/en/code-security/dependabot/dependabot-options-reference#cooldown-)
and [Renovate's npm security preset](https://docs.renovatebot.com/presets-security/#securityminimumreleaseagenpm).

Age is measured from publication metadata, never PR creation time. GitHub dependency
review supplies concrete version changes; deps.dev supplies public publication dates
for npm, NuGet, PyPI, Cargo, Go, and Maven. GitHub Actions references are matched to
GitHub releases by their actual commit. Docker Hub tags are accepted only when the full
PR digest matches Docker Hub's current digest, then use `tag_last_pushed`. Unsupported
MCR tags are accepted only when the full digest matches Microsoft's catalog record,
then use its `lastModifiedDate`. Unsupported or unavailable metadata holds the update
and is reported; Swift and private registries may require manual review.

Verified security fixes can bypass the delay when GitHub reports a vulnerable package
being replaced and no introduced version has known advisories. Labels and PR titles
do not grant this exception. Use `--wait-for-security-fixes` to require the delay too.

```json
"releaseAge": {
  "minimumDays": 7,
  "securityFixesBypass": true,
  "allowUnknown": false
}
```

The same object can be set within a repository profile. `--minimum-release-age-days`
changes the deployment default; `0` disables that gate. `--allow-unknown-age` explicitly
permits missing metadata. A cooldown reduces exposure but is not proof a package is
safe. Floating references can resolve new code without a PR; this gate evaluates
concrete versions reported for the PR being maintained.

## Limits

Use `run --help` for all options. Defaults are two attempts per lineage, 30 minutes
per repair, and 15 minutes waiting for CI. A sweep updates at most one PR per repository,
and rotates repair priority so a busy repository cannot monopolize the daily budget.
Use `--retry-blocked` for an explicit retry after correcting a blocker.

Persistent post-merge failures first receive one exact-commit CI retry. If that still
fails and `autoRecover` is enabled, Depkeeper asks Copilot for a focused repair, runs
the configured validation and Picket, and opens one recovery PR assigned to
`recoveryAssignee`. The recovery uses the normal attempt and daily repair budgets.
