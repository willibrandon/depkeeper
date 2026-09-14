# Authentication

Install .NET 10 and `gh`, then run:

```sh
dotnet run --file scripts/setup-auth.cs
```

Uses your GitHub login, verifies Copilot access, and configures Actions secrets.
Add `-- --repo OWNER/REPO` to choose another repository. Rerun to refresh credentials.
