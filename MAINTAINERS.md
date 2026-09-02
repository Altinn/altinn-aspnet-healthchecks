# Maintaining altinn-aspnet-healthchecks

This repository is set up so that the steady-state maintenance cost is close to zero: routine
dependency updates merge and release themselves, and a human is only pulled in where judgement is
actually required. This document explains how that works, what you still own, and how to
intervene when the automation is wrong.

## The steady state

```
Renovate opens a PR  ─┬─ non-major ──► CI green ──► automerges ──┐
                      └─ major ──────► you review ───────────────┤
                                                                 ▼
                            commit lands on main with a conventional message
                                                                 │
                              fix:/feat: ─────► release-please opens a release PR
                              chore:/ci:/test: ► no release; accumulates
                                                                 │
                                        release PR merges ──► tag v<x.y.z>
                                                                 │
                     publish job packs, exchanges OIDC for a key, pushes to nuget.org
```

Nothing in that path needs a human when a dependency bump is routine.

## What decides whether something releases

Releases are derived from commit messages ([Conventional Commits][cc]). `main` is squash-merged,
so **the PR title becomes the commit message** — the `PR title` workflow fails a PR whose title
is not conventional, because an unconventional title would otherwise silently produce no release.

| Commit type | Effect on the next release |
|---|---|
| `feat:` | minor bump (pre-1.0: minor, per `bump-minor-pre-major`) |
| `fix:`, `perf:` | patch bump |
| `chore:`, `ci:`, `test:`, `docs:`, `refactor:`, `style:` | no release |
| any type with `!` or a `BREAKING CHANGE:` footer | major bump (pre-1.0: minor) |

Renovate is configured to pick the type for you:

- **Shipped dependencies** (currently only `OpenTelemetry`, consumed by
  `Altinn.AspNet.HealthChecks.OpenTelemetry`) → `fix(deps):` → **patch release**. Consumers get a
  raised dependency floor, which is a real, user-visible change. This update is deliberately
  ungrouped so it always arrives as its own PR.
- **Test, sample, and CI dependencies** → `chore(deps):` → **no release**. These never reach a
  consumer, so republishing the packages would be noise.

### Why the release semantics live in renovate.json

Central Package Management means nearly every version lives in `Directory.Packages.props`, so
Renovate's path-based rules cannot tell a shipped dependency from a test one by file alone. The
`OpenTelemetry` rule is therefore explicit, and it is the rule to extend: **when you add a
`PackageReference` to a project under `src/`, add that package to the `fix(deps)` rule in
`renovate.json`.** Forgetting means its updates merge without cutting a release — recoverable
(see "Forcing a release") but easy to miss.

The same overlap cuts the other way for **lock file maintenance**. That branch refreshes every lock
file in the repo at once, so the `src/**` rule matches it and would type a no-op refresh as `fix`,
cutting a release of all four packages for a change no consumer can observe — lock files are not
part of a `.nupkg`. The last rule in `renovate.json` pins `lockFileMaintenance` to `chore`, and it
has to stay last to win. Keep new `fix(deps)` rules above it.

## What still needs you

Everything else is automated; these three things are not, by design.

1. **Major dependency updates.** Labelled `needs-review`, never automerged. You decide whether the
   update is safe and whether it forces a major of your own.
2. **SDK updates** (`global.json`). Labelled `needs-review` — bumping the SDK changes what every
   contributor and every CI run builds with.
3. **Deliberate API changes.** Package validation (below) will stop you from shipping a breaking
   change by accident, but it cannot decide for you that a break is intended.

## Package validation: the safety net that makes automerge safe

`EnablePackageValidation` is on for every packable project, and `dotnet pack` runs on every CI
build — so validation is a pull-request gate, not just a release-time check. It verifies that the
`net8.0`, `net9.0` and `net10.0` assemblies inside a package stay API-compatible with each other.

**Setting the baseline is a release-boundary operation.** In `src/Directory.Build.props`:

```xml
<PackageValidationBaselineVersion>0.2.0</PackageValidationBaselineVersion>
```

From then on the build additionally diffs against that published package and **fails on any
breaking change**.

Two rules keep this from tying itself in knots:

1. **Set it to the version that just shipped, immediately after shipping it** — never before.
   Enabling it *during* a release that contains breaking changes is circular: the baseline is
   still the older version the release deliberately breaks from, so the build fails on the very
   change you intended.
2. **A release that contains intentional breaks ships with the baseline pointing at the previous
   version, or unset.** Pre-1.0, that means each breaking release is "free" and the baseline is
   re-pointed afterwards.

**Adding a new package while a baseline is set** is the other trap: a brand-new project inherits
the repo-wide baseline and `dotnet pack` fails trying to download a version of itself that never
existed. Clear it in that project's csproj until it has shipped once:

```xml
<!-- Remove after this package's first release. -->
<PackageValidationBaselineVersion></PackageValidationBaselineVersion>
```

When it fails, you have exactly two honest options:

- The break was unintentional → fix the code.
- The break was intentional → land it with a `!`/`BREAKING CHANGE:` commit so release-please cuts
  the appropriate version, and bump `PackageValidationBaselineVersion` to that new version in the
  same release.

Suppressing a validation error without doing one of those two things ships a silent break to
consumers.

## Test coverage across target frameworks

The test project multi-targets `net8.0;net9.0;net10.0` — the same frameworks the packages ship
for — so the suite genuinely runs on each one (30 tests × 3 frameworks). This is what makes
unattended dependency merges defensible: an update that breaks `net8.0` fails CI instead of
reaching consumers.

CI installs the `8.0.x` and `9.0.x` runtimes alongside the SDK pinned in `global.json`. To run
the full matrix locally you need the same:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --runtime aspnetcore
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --runtime aspnetcore
```

Without them, `dotnet test` runs only the `net10.0` leg and reports a non-zero exit code.

`Microsoft.AspNetCore.TestHost` is versioned in lockstep with the shared framework, so
`Directory.Packages.props` carries one version per framework and `renovate.json` pins each line to
its own major band. **Do not "consolidate" those three lines** — the `net8.0` and `net9.0` legs
stop building if any of them rolls forward.

## Lock files

`RestorePackagesWithLockFile` is on and CI restores with `--locked-mode`, so a lock file that
drifts out of sync with the project files fails loudly instead of restoring something undescribed.

Renovate regenerates `packages.lock.json` itself (via `dotnet restore --force-evaluate`), which it
can only do if its runner has an SDK matching `global.json`. **Watch the first few Renovate PRs.**
If they fail on `NU1004`, the fastest fixes are, in order of preference:

1. Relax `global.json` (`rollForward` is already `latestFeature`) so Renovate's SDK qualifies.
2. Drop `RestorePackagesWithLockFile`. For a library, lock files only affect CI reproducibility —
   they do not flow to consumers — so this is a cheap trade if they become a recurring tax.

To regenerate by hand:

```bash
dotnet restore --force-evaluate
```

## Publishing: Trusted Publishing, no API keys

Publishing uses [Trusted Publishing][tp]. There is **no `NUGET_API_KEY` secret**, and there never
should be — long-lived API keys are discouraged and are the single most valuable thing an attacker
could take from this repository.

Instead, the `publish` job requests a short-lived OIDC token from GitHub (`permissions:
id-token: write`), `NuGet/login@v1` exchanges it with nuget.org for a temporary API key, and
`dotnet nuget push` uses that. The key is valid for **one hour**, and each OIDC token can be
exchanged **exactly once** — which is why the login step sits immediately before the push rather
than at the top of the job.

### Why everything that publishes lives in one workflow file

A nuget.org policy is bound to a single **workflow file name**, and nuget.org validates the
workflow in which the token was obtained. Reusable workflows make that claim ambiguous —
`workflow_ref` names the caller while `job_workflow_ref` names the callee — and are a known cause
of `401 … No matching trust policy` failures ([NuGet/login#6][login6], still open).

So `release.yml` performs the login and push inline and is the *only* workflow that publishes. It
handles all three entry paths (release-please, manual dispatch, hand-pushed tag) itself.

**Do not split the publish steps into a reusable workflow**, and do not add a second publishing
workflow — either would break token exchange or require a second policy. If you ever do rename
`release.yml`, the nuget.org policy must be updated in the same change or publishing breaks.

### Creating the policy

On nuget.org → your username → **Trusted Publishing** → add a policy:

| Field | Value |
|---|---|
| Repository Owner | `Altinn` |
| Repository | `altinn-aspnet-healthchecks` |
| Workflow File | `release.yml` — file name only, no `.github/workflows/` prefix |
| Environment | *(leave empty — this repo uses no GitHub Actions environment)* |

Choose the **owner** carefully: the policy applies to all packages owned by that user or
organization. Prefer the organization that owns the packages over an individual, so the policy
does not go inactive when one person leaves. (An org-owned policy also becomes inactive if its
creator is removed from the org, and reactivates when they are added back.)

### The 7-day pending window

A new policy may start out **temporarily active for 7 days**, typically for private repositories.
nuget.org needs the numeric GitHub repository and owner IDs — which only arrive with a real
publish — to pin the policy against resurrection attacks (delete a repo, recreate it under the
same name, publish as if nothing changed).

**If no publish happens within those 7 days, the policy goes inactive.** That is fine and
recoverable: restart the window from the UI at any time, even after it has expired. Just don't
create the policy long before you intend to release.

## One-time setup

The automation is fully wired but depends on settings that live outside this repo:

- [ ] **Trusted Publishing policy on nuget.org**, as above.
- [ ] **Variable `NUGET_USER`** — the nuget.org account *username* (profile name, **not** an email
      address) that owns the trusted publishing policy. A variable rather than a secret: it is
      public information and confers nothing by itself, and leaving it unmasked is what lets a
      failed token exchange name the account it tried instead of reporting `'***'`.
- [ ] **Repository setting: "Allow auto-merge"** — required for Renovate's `platformAutomerge` and
      for the release PR to merge itself.
- [ ] **Branch protection on `main`** requiring the `build-and-test` check. Automerge waits for
      required checks; without protection, "automerge" means "merge as soon as the PR is opened",
      which throws away the safety net entirely.
- [ ] **A GitHub App for release-please**, providing variable `RELEASE_PLEASE_APP_CLIENT_ID` and
      secret `RELEASE_PLEASE_APP_PRIVATE_KEY` — see below.
- [ ] **First release cut by hand.** The manifest is bootstrapped at `0.1.0`; run the `Release`
      workflow manually with version `0.1.0`, or push a `v0.1.0` tag. Do this within the policy's
      7-day window. From the next merge on, release-please takes over.

### Why release-please needs its own token

GitHub deliberately does not raise workflow events for actions taken by `GITHUB_TOKEN`. Two
consequences:

- **A release PR opened with `GITHUB_TOKEN` gets no CI run**, so required checks are never
  satisfied and it can never auto-merge. A GitHub App installation token does trigger workflows,
  which is what makes the pipeline fully unattended. Without the App everything still works — the
  release PR just needs one human click.
- **The release tag pushed by release-please does not trigger the `v*` tag path** either. That is
  already handled regardless of token: the `publish` job runs in the same workflow run as
  release-please, gated on its `release_created` output, rather than waiting for a tag event.

### Setting up the GitHub App

A GitHub App is preferred over a personal access token: it belongs to the organization rather
than to a person, so it does not break when someone leaves; it is scoped to this repository
alone; and the token it mints lives one hour and is revoked when the job ends, rather than
sitting in the secret store indefinitely.

**1. Register the App.** Organization → **Settings** → **Developer settings** → **GitHub Apps** →
**New GitHub App**. Registering it under the `Altinn` org (not a personal account) is the point of
the exercise.

- **Name:** anything unused org-wide, e.g. `altinn-release-please`. This becomes the PR author.
- **Homepage URL:** the repository URL is fine.
- **Webhook:** **uncheck "Active"**. The App is only ever used to mint tokens; it receives nothing.
- **Repository permissions:**

  | Permission | Access | Why |
  |---|---|---|
  | Contents | Read and write | Commit the release PR, create tags and releases |
  | Pull requests | Read and write | Open the release PR and enable auto-merge |
  | Issues | Read and write | Labels on the release PR go through the issues API |

  Nothing else. In particular **do not grant Workflows** — the release PR never touches
  `.github/workflows/`, and that permission would let a compromised key rewrite this pipeline.
- **Where can this App be installed:** "Only on this account".

**2. Collect the credentials.** On the App's settings page:

- Copy the **Client ID** (shown near the top).
- **Generate a private key** — this downloads a `.pem` file. GitHub shows it once; if lost, revoke
  and generate a new one rather than trying to recover it.

**3. Install it.** On the App page → **Install App** → pick the org → **Only select repositories**
→ `altinn-aspnet-healthchecks`.

**4. Wire it into the repository.** Repository → **Settings** → **Secrets and variables** →
**Actions**:

- **Variables** tab → new variable `RELEASE_PLEASE_APP_CLIENT_ID` = the Client ID. A variable,
  not a secret: it is not sensitive, and unlike secrets it can be read from a step-level `if`,
  which is how the workflow detects whether the App is configured.
- **Secrets** tab → new secret `RELEASE_PLEASE_APP_PRIVATE_KEY` = **the entire contents** of the
  `.pem` file, including the `-----BEGIN RSA PRIVATE KEY-----` and `-----END …-----` lines.

`actions/create-github-app-token@v3` exchanges these for an installation token at the start of
each release run, narrowing it to the three permissions above.

**Rotation:** private keys do not expire, but if one leaks, generate a new key on the App page,
update the secret, then delete the old key. No workflow change is needed.

[tp]: https://learn.microsoft.com/nuget/nuget-org/trusted-publishing
[login6]: https://github.com/NuGet/login/issues/6

## Common interventions

**Forcing a release** (e.g. a `chore(deps)` update turned out to be consumer-visible):

```bash
git commit --allow-empty -m "fix: release <package> with updated <dependency>"
```

Push to `main` via a PR; release-please picks it up.

**Publishing out of band** — run the `Release` workflow with an explicit version input. It builds,
tests, packs, and pushes without touching release-please's state, so remember to keep
`.release-please-manifest.json` in step afterwards.

**Token exchange fails with `401 … No matching trust policy owned by user was found`** — work
through, in order: the policy's owner matches `NUGET_USER`; the workflow file field is exactly
`release.yml` with no path prefix; the policy has not gone inactive (expired 7-day window, or its
creator left the owning organization); and nothing has moved the login/push steps into a reusable
workflow. See "Why everything that publishes lives in one workflow file".

**A bad release reached nuget.org** — do not delete it; nuget.org only supports unlisting, and
deletion would break consumers who already restored it. Unlist the version and ship a fix forward.

## Adding a companion package

See the README. Two automation-specific additions:

1. If the new project takes a `PackageReference`, add that package to the `fix(deps)` rule in
   `renovate.json` so its updates cut releases.
2. Once the package has a first release, it is covered by the shared
   `PackageValidationBaselineVersion` automatically — no per-project configuration.

[cc]: https://www.conventionalcommits.org/
