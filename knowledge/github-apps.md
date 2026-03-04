# GitHub Apps

## What is a GitHub App?

A GitHub App is a first-class actor on GitHub — it has its own identity, its own permissions, and acts on behalf of itself rather than a user. Unlike OAuth Apps or personal access tokens, a GitHub App:

- Is installed on a specific account (user or org) and can be scoped to specific repositories
- Uses short-lived installation access tokens (1 hour), not long-lived user tokens
- Has fine-grained permissions declared upfront in a manifest or app settings
- Shows up in audit logs as the app name, not a user

## App vs Installation

Two distinct concepts:

- **App** — the registered application (has an App ID, private key, client ID/secret). Owned by a user or org account.
- **Installation** — the act of installing that app on a target account/repo. Each installation has its own Installation ID and generates its own access tokens.

One app can have many installations (e.g. installed on multiple orgs or repos).

## Authentication Flow

```
App private key
  → sign JWT (valid 10 min)
    → POST /app/installations/{installation_id}/access_tokens
      → installation access token (valid 1 hour)
        → use as Bearer token for API calls
```

The JWT authenticates as the app itself. The installation token authenticates as the app acting within a specific installation.

## Manifest Flow (how Orca registers apps)

The [GitHub App Manifest flow](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest) allows programmatic app creation without manual UI steps:

1. Build a JSON manifest describing the app (name, permissions, redirect URL, etc.)
2. POST the manifest to `https://github.com/settings/apps/new` (user) or `https://github.com/organizations/{org}/settings/apps/new` (org) via an auto-submitting HTML form
3. GitHub redirects back to the `redirect_url` with a temporary `?code=`
4. Exchange the code: `POST https://api.github.com/app-manifests/{code}/conversions`
5. Response contains the App ID, PEM private key, webhook secret, and owner info

The conversion response includes an `owner` object:
```json
{
  "owner": {
    "login": "my-org",
    "type": "Organization"
  }
}
```
`type` is either `"Organization"` or `"User"`. This is the authoritative source for whether the app is owned by an org or a personal account.

## Permissions

Permissions are declared in two categories:

### Repository permissions
Scoped to repositories the app is installed on. Examples:
- `issues: write`
- `pull_requests: read`
- `metadata: read` (always required, read-only)
- `contents: read`

### Organization permissions
Scoped to the organisation itself, not individual repos. Examples:
- `members: read`
- `projects: write` — required for org-level GitHub Projects (classic and new)

**Important:** The manifest flow cannot set organisation permissions. They must be granted manually after app creation.

## Permission Setup: User vs Org

### Personal account app

App settings URL:
```
https://github.com/settings/apps/{app-slug}/permissions
```

- Repository permissions work immediately after installation
- There are no org-level permissions to configure
- Install the app at `https://github.com/settings/apps/{app-slug}/installations`

### Organisation app

App settings URL:
```
https://github.com/organizations/{org}/settings/apps/{app-slug}/permissions
```

After creation via the manifest flow, you must manually grant org permissions:

1. Go to the permissions URL above
2. Scroll to **Organization permissions**
3. Set **Projects** to `Read and write` (required for Orca to manage org-level projects)
4. Click **Save changes** and confirm the permission request

The app slug in the URL comes from the `slug` field in the manifest conversion response — this may differ from the `name` field (GitHub lowercases and hyphenates it).

### Key differences

| | Personal account | Organisation |
|---|---|---|
| App registration URL | `/settings/apps/new` | `/organizations/{org}/settings/apps/new` |
| App settings URL | `/settings/apps/{slug}` | `/organizations/{org}/settings/apps/{slug}` |
| Permissions URL | `/settings/apps/{slug}/permissions` | `/organizations/{org}/settings/apps/{slug}/permissions` |
| Org-level permissions | Not applicable | Must be set manually post-creation |
| Installation URL | `/settings/apps/{slug}/installations` | `/organizations/{org}/settings/apps/{slug}/installations` |
| `owner.type` in API | `"User"` | `"Organization"` |

## Installation

After creating and configuring the app, install it:

- **Personal:** `https://github.com/settings/apps/{app-slug}/installations`
- **Org:** `https://github.com/organizations/{org}/settings/apps/{app-slug}/installations`

Once installed, the Installation ID is available via:
```
GET https://api.github.com/app/installations
```
(authenticated with a JWT). Store this ID — it is needed to generate installation access tokens.

## Relevant API Endpoints

| Purpose | Method | URL |
|---|---|---|
| Exchange manifest code | POST | `https://api.github.com/app-manifests/{code}/conversions` |
| List installations | GET | `https://api.github.com/app/installations` |
| Create installation token | POST | `https://api.github.com/app/installations/{id}/access_tokens` |
| Get authenticated app | GET | `https://api.github.com/app` |

All app-level requests require a JWT in the `Authorization: Bearer {jwt}` header. Installation token requests use the resulting token.
