# Depkeeper

Daily dependency maintenance powered by GitHub Copilot and .NET.

Depkeeper discovers Dependabot PRs, repairs failed checks, merges verified updates,
and reports blockers. It runs in GitHub Actions with your Copilot subscription.

- Checks the exact PR revision and GitHub merge requirements.
- Uses Copilot's native editing tools and isolated containers for commands.
- Verifies repairs independently and scans changes before pushing.
- Refreshes stale PR checks and verifies the exact merge commit.
- Creates one bounded recovery PR when persistent post-merge CI can be repaired safely.
- Remembers blocked revisions and limits repair attempts.
- Applies a configurable publication cooldown, defaulting to three days.
- Supports .NET, Node, Rust, Go, Python, JVM projects, and custom toolchains.

## Setup

With the latest .NET 10 SDK and `gh` installed:

```sh
dotnet run --file scripts/setup-auth.cs
gh variable set DEPKEEPER_MODEL --body gpt-5.6-sol
gh variable set DEPKEEPER_REPOSITORIES --body '["OWNER/REPOSITORY"]'
```

The **Maintenance** workflow runs daily at **09:17 UTC**. Its manual trigger
defaults to a dry run. Each deployment uses its own account and repository list.

## Run locally

```sh
dotnet run --project src/Depkeeper.Cli -- run --repository OWNER/REPOSITORY --dry-run
dotnet run --project src/Depkeeper.Cli -- models
```

Omit `--dry-run` to permit repairs and merges. Repairs require Docker and `picket`
on PATH. [Configuration](docs/configuration.md) covers profiles, limits, and cooldowns.

## Develop

```sh
dotnet build
dotnet test --solution Depkeeper.slnx
dotnet format --verify-no-changes
```

The opt-in `repair-smoke` command exercises real Copilot tool use against a
disposable fixture. [Operation](docs/operation.md) describes checkpoints and reports.

## License

[MIT](LICENSE)
