# Depkeeper

Daily dependency maintenance powered by GitHub Copilot.

## Design

Depkeeper will discover Dependabot PRs, repair failed checks where possible,
merge verified updates, and report blockers. It runs in GitHub Actions using
the GitHub Copilot SDK for .NET.

- Configurable repositories and validation commands.
- Bounded repair attempts and persistent tracking of blocked work.
- Deterministic CI and exact-commit checks before merging.
- Concise reports with actionable failures.

Each owner runs their own instance using their own GitHub and Copilot access.

## Develop

Use the latest .NET 10 SDK.

```sh
dotnet restore
dotnet build --no-restore
dotnet test --solution Depkeeper.slnx
dotnet run --project src/Depkeeper.Cli -- --help
```

After setting `COPILOT_GITHUB_TOKEN`, list the models available to your account:

```sh
dotnet run --project src/Depkeeper.Cli -- models
```

## Setup

See [authentication](docs/authentication.md) for the required GitHub Actions
secrets. An example configuration is in [examples/depkeeper.yml](examples/depkeeper.yml).

## License

[MIT](LICENSE)
