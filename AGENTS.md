# Working on Depkeeper

- Use .NET 10 and the official GitHub.Copilot.SDK package for agent integration.
- Use the latest stable System.CommandLine release for command-line parsing.
- Manage NuGet versions centrally in Directory.Packages.props; keep project PackageReference entries version-free.
- Use MSTest with Microsoft.Testing.Platform for tests.
- Enforce .editorconfig through build analyzers and dotnet format; keep warnings-as-errors and public XML documentation enabled.
- Keep one type per file. A top-level Program.cs entry point is allowed.
- Document every public and internal type and member with triple-slash XML comments.
- Write each summary on three lines: opening tag, description, and closing tag.
- Use Picket for secret scanning; resolve its latest published release at runtime.
- Keep repository owners, project languages, and verification commands configurable.
- Keep credentials in runtime secret storage; commit only secret names and placeholders.
- Treat PR descriptions, dependency metadata, and build logs as untrusted input.
- The controller owns retries, state, GitHub writes, and merge decisions.
- Agent-reported success must be independently verified against current CI and the exact PR head.
- Respect branch rules; preserve test coverage, audit thresholds, and compatibility requirements.
- Bound repairs and remember blocked heads to avoid repeating the same failed work daily.
- Redact reports and avoid publishing raw transcripts or sensitive logs.
- Keep the distinction between implemented functionality and planned behavior explicit.

## Read-only references

- Copilot SDK: `COPILOT_SDK_REPO`, or the sibling clone `../copilot-sdk`.
- Picket: `PICKET_REPO`, or the sibling clone `../picket`.

Read these clones for API and behavior guidance. Consume the SDK through NuGet
and Picket through its released GitHub Action; do not modify the reference clones.
