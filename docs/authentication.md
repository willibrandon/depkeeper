# Authentication

Create two **fine-grained personal access tokens** at
<https://github.com/settings/personal-access-tokens/new>.
Use your personal GitHub account as the resource owner.

## Copilot inference

Secret name: **`COPILOT_GITHUB_TOKEN`**

- Account permission: **Copilot Requests — Read**.
- No repository write permissions are needed for this token.
- Copilot CLI access must be permitted by the organization providing your license.

## Repository maintenance

Secret name: **`GH_MAINTENANCE_TOKEN`**

Select only this deployment repository and the repositories it will maintain.

| Repository permission | Access |
| --- | --- |
| Contents | Read and write |
| Pull requests | Read and write |
| Issues | Read and write |
| Actions | Read and write |
| Workflows | Read and write |
| Checks | Read-only |
| Commit statuses | Read-only |
| Metadata | Read-only, automatically included |

Workflows access enables repairs to GitHub Actions dependency updates.
Repository administration and secret-management permissions are not needed.

## Store the tokens

Run these commands locally. Each prompts for the value without placing it in
the command itself:

```sh
gh secret set COPILOT_GITHUB_TOKEN --repo OWNER/depkeeper
gh secret set GH_MAINTENANCE_TOKEN --repo OWNER/depkeeper
```

Keep token values in GitHub Actions secrets. Configuration files contain only
secret names. Agent repair jobs should have read-only repository credentials;
the maintenance credential belongs in the separate publishing and merge jobs.

[Copilot SDK authentication reference](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/authenticate)
