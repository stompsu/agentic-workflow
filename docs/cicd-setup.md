# CI/CD setup (GitHub Environments + Azure OIDC)

Owner: devops-pipelines. Every step below is **run by the user**. Nothing here has been executed by an agent, and no
agent creates identities, federated credentials, GitHub environments or variables.

Pipelines authenticate to Azure with OpenID Connect (`azure/login` with client-id / tenant-id / subscription-id). There
are no long-lived Azure secrets in GitHub. Everything stored in GitHub is a non-secret **environment variable**.

## 0. Prerequisites (run by the user)

- Resource group per environment already exists (the infra deployment is scoped to it).
- `az login` as someone who may create identities and role assignments (Owner / User Access Administrator).
- `gh auth login` as a repository admin.
- Decide whether the infra workflow deploys infrastructure (needs role-assignment rights, section 5) or whether you
  deploy infra from your machine.

## 1. Overview of what gets created, per environment (dev, test, prod)

| Item | Name / value |
|---|---|
| GitHub Environment | `dev`, `test`, `prod` |
| User-assigned managed identity (pipeline identity, NOT the app identity) | `id-github-<env>` in the environment RG |
| Federated credential | name `github-<env>`; issuer `https://token.actions.githubusercontent.com`; audience `api://AzureADTokenExchange`; subject `repo:<org>/<repo>:environment:<env>` |
| Roles | `Contributor` on the environment RG; `AcrPush` on the shared ACR for the **dev** identity only |
| Variables | see section 4 |

The shared ACR is created by the dev infra deployment (`createAcr = true`). Apps pull images with their own managed
identity (AcrPull in Bicep), not with the pipeline identity.

## 2. GitHub Environments (run by the user)

Script (dry-run by default): `scripts/setup-github-oidc.sh`. It creates each environment and restricts deployments to the
`main` branch via the REST API. Required reviewers are a portal step:

1. Settings > Environments > `test` > **Required reviewers**: add yourself / the approvers. Same for `prod`.
2. Settings > Environments > each env > **Deployment branches and tags**: selected branches = `main`
   (the script does this only for environments it creates).

The script never modifies an environment that already exists: GitHub's "Create or update an environment" endpoint
does not document what happens to omitted fields such as `reviewers` on update
(https://docs.github.com/en/rest/deployments/environments), so for existing environments verify reviewers and the
branch policy here by hand.
3. Optional: prod "Wait timer" and "Prevent self-review".

Note: pull-request workflows cannot use these environments because of the `main` restriction. That is why
`infra-validate.yml` only lints on pull requests (bicep build and build-params, no Azure login). `infra.yml` runs
what-if + deploy only by manual dispatch from `main`; nothing infra-related runs automatically.

## 3. Azure identities, federated credentials, roles (run by the user)

Review, then run the dry-run first, then apply:

```bash
# 1. dry run (default): prints the commands, executes nothing
scripts/setup-github-oidc.sh --org <org> --repo <repo> --acr <acr-name> --acr-rg <dev-rg> \
  --rg-dev <rg-dev> --rg-test <rg-test> --rg-prod <rg-prod>

# 2. when satisfied (run by the user)
scripts/setup-github-oidc.sh ... --apply
```

Add `--infra-roles` only if the infra workflow should deploy (section 5). The underlying commands, per environment:

```bash
az identity create -g $RG -n id-github-$ENV                                       # run by the user
az identity federated-credential create --name github-$ENV --identity-name id-github-$ENV -g $RG \
  --issuer https://token.actions.githubusercontent.com \
  --subject repo:$ORG/$REPO:environment:$ENV --audiences api://AzureADTokenExchange   # run by the user
az role assignment create --assignee-object-id $PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role Contributor --scope $RG_ID                                                # run by the user
az role assignment create --assignee-object-id $DEV_PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role AcrPush --scope $ACR_ID                                                   # run by the user, dev identity only
```

The dev ACR only exists after the first dev infra deployment. If you have not deployed infra yet, deploy dev from your
machine first (section 5, alternative), then run the setup script.

## 4. GitHub environment variables (run by the user)

Set per environment (`gh variable set NAME --env <env> --body <value>`). The script sets the first five; the rest come
from the Bicep deployment outputs after the first infra deploy.

| Variable | Source | Envs |
|---|---|---|
| `AZURE_CLIENT_ID` | client id of `id-github-<env>` (NOT the Bicep `managedIdentityClientId`) | all |
| `AZURE_TENANT_ID` | `az account show --query tenantId` | all |
| `AZURE_SUBSCRIPTION_ID` | `az account show --query id` | all |
| `AZURE_RESOURCE_GROUP` | output `resourceGroup` | all |
| `ACR_NAME` | output `acrName` (same shared registry everywhere) | all |
| `BACKEND_APP` | output `backendAppName` | all |
| `FRONTEND_APP` | output `frontendAppName` | all |
| `MI_SMOKE_JOB` | output `smokeJobName` (requires `deploySmokeJob = true`) | **test only; required, it is the prod gate** |
| `INGESTION_JOB` | output `ingestionJobName` | optional, any env |

Example (run by the user):
```bash
az deployment group show -g $RG -n <deployment-name> --query properties.outputs
gh variable set BACKEND_APP --env test --body <backendAppName>
```
No secrets, keys or connection strings go into variables.

## 5. Roles the infra workflow needs, and the alternative

`Contributor` is enough for the deploy workflow (update apps and jobs, start the smoke job). It is NOT enough for
`infra.yml`, because the Bicep creates role assignments (see `infra/README.md`, "Roles for the pipeline identity").
Grant these only if you want the pipeline to deploy infra (`--infra-roles` in the script does the first two; Key Vault Secrets Officer is skipped for prod):

| Role | Scope | Why |
|---|---|---|
| `Role Based Access Control Administrator` (or `User Access Administrator`) | environment RG | role assignments in Bicep |
| `Key Vault Secrets Officer` | environment RG (dev, test only; prod has no Key Vault secrets) | writes `cosmos-key`, `search-query-key`, `search-admin-key` (RBAC vault) |
| role-assignment rights | shared ACR (test, prod) | `acr-pull` module; or set `assignAcrPull = false` and assign AcrPull once by hand |
| role-assignment rights | Foundry project / model account | only when `foundryProjectResourceId` / `modelResourceId` are set |

Safer alternative (suggested for v1; your decision): do not grant these. Deploy infra from your machine as yourself:
```bash
infra/scripts/check-params.sh infra/params/<env>.bicepparam
az deployment group what-if -g <rg> --parameters infra/params/<env>.bicepparam backendImage=<current> frontendImage=<current>
az deployment group create  -g <rg> --parameters infra/params/<env>.bicepparam backendImage=<current> frontendImage=<current>
```
(run by the user). Pass the currently deployed images, otherwise the apps reset to the placeholder image. With this
alternative nobody dispatches `infra.yml`; pull requests still get the Bicep lint from `infra-validate.yml`, so there
are no automatic infra runs and the pipeline identity needs no infra roles.

## 6. Pipelines at a glance

| Workflow | Trigger | What |
|---|---|---|
| `ci.yml` | pull request, called by deploy | backend build/test, frontend lint/typecheck/test/build, Playwright e2e (mocked auth + API), bicep lint, docker build validation for 3 images (no push; ingestion uses the repo root as build context) |
| `deploy.yml` | push to `main`, manual | ci -> build + push 3 images once (tag = short SHA, in `dev`) -> deploy dev -> deploy test (approval, then MI smoke job) -> deploy prod (approval) |
| `_deploy-env.yml` | called | backend, then frontend, revision suffix `r<sha>-<run>-<attempt>`, smoke `GET https://<frontend fqdn>/api/healthz`; test: MI smoke job |
| `infra.yml` | manual only (from `main`) | check-params, resolve deployed images, what-if, re-resolve images, deploy |
| `infra-validate.yml` | pull request under `infra/` | bicep build + build-params, no Azure login |
| `ingestion.yml` | manual | start the ingestion job in one environment and wait |

Prod gate: `deploy-prod` needs `deploy-test`, whose last step starts the MI smoke job (`--smoke-check`, managed
identity, no keys) with the same image tag and fails the run on a non-success status. Prod approval is only requested
after that passes. If `MI_SMOKE_JOB` is missing in `test`, the gate fails (it never silently skips).

Rollback: re-run the Deploy workflow on the previous good commit (that tag is already in ACR), or dispatch it from main
after reverting. The same smoke gates apply.

First-time order: infra dev (from your machine) -> setup script -> set app/job variables -> infra test/prod -> merge to
`main` to trigger the first deploy.

## 7. Troubleshooting

- `AADSTS700213` / no matching federated identity record: the subject must be exactly
  `repo:<org>/<repo>:environment:<env>` (case-sensitive org/repo) and the job must declare `environment:`.
- Deploy job waits forever: pending approval in the environment.
- "Branch not allowed to deploy": the run is not on `main`.
- Role assignment errors right after creating the identity: propagation, retry after a minute.
- `check-params` fails in infra.yml (or locally): fill the `entra*`, client id and scope placeholders in `infra/params/<env>.bicepparam`.
