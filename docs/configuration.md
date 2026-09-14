# Configuration

Deployment settings use GitHub Actions repository variables:

| Variable | Value |
| --- | --- |
| `DEPKEEPER_MODEL` | Copilot model ID, such as `gpt-6-astra` |
| `DEPKEEPER_REPOSITORIES` | JSON array of `OWNER/REPO` names |

```sh
gh variable set DEPKEEPER_MODEL --body gpt-6-astra
gh variable set DEPKEEPER_REPOSITORIES --body '["OWNER/REPO"]'
```

Run these from your deployment repository, or add `--repo OWNER/depkeeper`.
Use `dotnet run --project src/Depkeeper.Cli -- models` to list available model IDs.
