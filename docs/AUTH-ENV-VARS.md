# OrcAI CLI — Authentication Environment Variables

Sensitive auth settings can be supplied via environment variables instead of (or in addition to) the stored config file at `~/.config/orcai/auth.json`. Environment variables silently take precedence over the stored file config when set.

---

## PAT authentication

| Environment variable | Description |
|---|---|
| `ORCAI_PAT` | GitHub Personal Access Token. When set, the stored config file is not read. |

### Example

```sh
export ORCAI_PAT=ghp_xxxxxxxxxxxxxxxxxxxx
orcai run job.yml
```

### PAT as a secondary credential for `@copilot` assignment

GitHub Apps cannot assign `@copilot` to issues. If your primary auth is a GitHub App, set `ORCAI_PAT` (or store a `pat` profile via `orcai auth pat`) alongside your App credentials. OrcAI will use the PAT only for the `@copilot` assignment step and the App token for everything else.

The PAT requires the following permissions:

| Permission | Level |
|---|---|
| Issues | Read & write |
| Metadata | Read (implicit) |

For an org, use a fine-grained PAT scoped to the org or specific repositories, or a classic PAT with `repo` scope.

If `ORCAI_PAT` is not set and primary auth is a GitHub App, OrcAI will skip `@copilot` assignment and print a warning:

```
Warning: primary auth is a GitHub App which cannot assign @copilot.
Set ORCAI_PAT or add a 'pat' profile to auth.json to enable Copilot assignment.
```

#### Example — GitHub App + PAT in CI

```sh
export ORCAI_APP_ID=123456
export ORCAI_APP_INSTALLATION_ID=78901234
export ORCAI_APP_PRIVATE_KEY="$(cat /run/secrets/orcai-app-key.pem)"
export ORCAI_PAT=ghp_xxxxxxxxxxxxxxxxxxxx
orcai run job.yml
```

---

## GitHub App authentication

| Environment variable | Description |
|---|---|
| `ORCAI_APP_ID` | GitHub App ID. Overrides `appId` in the stored config file. |
| `ORCAI_APP_INSTALLATION_ID` | Installation ID for the target organisation. Overrides `installationId` in the stored config file. |
| `ORCAI_APP_PRIVATE_KEY` | Raw PEM content of the App private key. When set, no key file is read from disk. Takes precedence over `ORCAI_APP_KEY_PATH`. |
| `ORCAI_APP_KEY_PATH` | Path to the PEM private key file. Overrides `keyPath` in the stored config file. Ignored if `ORCAI_APP_PRIVATE_KEY` is set. |

### Precedence for the private key

```
ORCAI_APP_PRIVATE_KEY (raw PEM)  ← highest priority
ORCAI_APP_KEY_PATH (file path)
stored keyPath in auth.json      ← lowest priority
```

### Example — CI pipeline using all env vars (no file config needed)

```sh
export ORCAI_APP_ID=123456
export ORCAI_APP_INSTALLATION_ID=78901234
export ORCAI_APP_PRIVATE_KEY="$(cat /run/secrets/orcai-app-key.pem)"
orcai run job.yml
```

### Example — partial override (key from env, other values from stored config)

```sh
# Stored config already has appId and installationId.
export ORCAI_APP_PRIVATE_KEY="$(cat /run/secrets/orcai-app-key.pem)"
orcai run job.yml
```

---

## Precedence summary

For each setting, the lookup order is:

1. Environment variable (if non-empty)
2. Stored config file (`~/.config/orcai/auth.json`)
3. Error — OrcAI exits with a descriptive message

No message is printed when an environment variable overrides a file value.

---

## Storing config to file

The `orcai auth` commands always write to the config file regardless of whether env vars are present. Env var resolution happens at command-execution time, not at store time.

```sh
# Store a PAT to file (env var can still override it at run time)
orcai auth pat --token ghp_xxxxxxxxxxxxxxxxxxxx

# Store App config to file
orcai auth app --app-id 123456 --key /path/to/key.pem --installation-id 78901234
```
