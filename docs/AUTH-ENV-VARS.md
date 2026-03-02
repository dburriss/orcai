# Orca CLI — Authentication Environment Variables

Sensitive auth settings can be supplied via environment variables instead of (or in addition to) the stored config file at `~/.config/orca/auth.json`. Environment variables silently take precedence over the stored file config when set.

---

## PAT authentication

| Environment variable | Description |
|---|---|
| `ORCA_PAT` | GitHub Personal Access Token. When set, the stored config file is not read. |

### Example

```sh
export ORCA_PAT=ghp_xxxxxxxxxxxxxxxxxxxx
orca run job.yml
```

---

## GitHub App authentication

| Environment variable | Description |
|---|---|
| `ORCA_APP_ID` | GitHub App ID. Overrides `appId` in the stored config file. |
| `ORCA_APP_INSTALLATION_ID` | Installation ID for the target organisation. Overrides `installationId` in the stored config file. |
| `ORCA_APP_PRIVATE_KEY` | Raw PEM content of the App private key. When set, no key file is read from disk. Takes precedence over `ORCA_APP_KEY_PATH`. |
| `ORCA_APP_KEY_PATH` | Path to the PEM private key file. Overrides `keyPath` in the stored config file. Ignored if `ORCA_APP_PRIVATE_KEY` is set. |

### Precedence for the private key

```
ORCA_APP_PRIVATE_KEY (raw PEM)  ← highest priority
ORCA_APP_KEY_PATH (file path)
stored keyPath in auth.json     ← lowest priority
```

### Example — CI pipeline using all env vars (no file config needed)

```sh
export ORCA_APP_ID=123456
export ORCA_APP_INSTALLATION_ID=78901234
export ORCA_APP_PRIVATE_KEY="$(cat /run/secrets/orca-app-key.pem)"
orca run job.yml
```

### Example — partial override (key from env, other values from stored config)

```sh
# Stored config already has appId and installationId.
export ORCA_APP_PRIVATE_KEY="$(cat /run/secrets/orca-app-key.pem)"
orca run job.yml
```

---

## Precedence summary

For each setting, the lookup order is:

1. Environment variable (if non-empty)
2. Stored config file (`~/.config/orca/auth.json`)
3. Error — Orca exits with a descriptive message

No message is printed when an environment variable overrides a file value.

---

## Storing config to file

The `orca auth` commands always write to the config file regardless of whether env vars are present. Env var resolution happens at command-execution time, not at store time.

```sh
# Store a PAT to file (env var can still override it at run time)
orca auth pat --token ghp_xxxxxxxxxxxxxxxxxxxx

# Store App config to file
orca auth app --app-id 123456 --key /path/to/key.pem --installation-id 78901234
```
