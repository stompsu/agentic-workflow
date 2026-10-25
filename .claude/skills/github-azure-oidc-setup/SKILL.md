---
name: github-azure-oidc-setup
description: One-time setup of GitHub Environments and Azure federated credentials (OIDC) for the pipelines. Use to generate /docs/cicd-setup.md and the setup commands. Never execute them.
---
Produce /docs/cicd-setup.md containing, per environment (dev, test, prod):
1. GitHub Environment: create it; for test and prod add required reviewers and restrict deployment branches to main (portal step).
2. Azure identity: one user-assigned managed identity per environment with a federated credential:
   issuer https://token.actions.githubusercontent.com, audience api://AzureADTokenExchange,
   subject repo:<org>/<repo>:environment:<env>.
3. Roles: Contributor on that environment's resource group (tighten later); AcrPush on the shared ACR for the dev identity only;
   Owner or User Access Administrator on the RG ONLY if the infra workflow must create role assignments.
4. GitHub environment variables: the seven names from the github-actions-pipelines skill.

Provide the commands as a script for the user to review and run themselves, for example:
  az identity create -g $RG -n id-github-$ENV
  az identity federated-credential create --name github-$ENV --identity-name id-github-$ENV -g $RG \
    --issuer https://token.actions.githubusercontent.com \
    --subject repo:$ORG/$REPO:environment:$ENV --audiences api://AzureADTokenExchange
  az role assignment create --assignee-object-id $PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
    --role Contributor --scope $RG_ID
  gh variable set AZURE_CLIENT_ID --env $ENV --body $CLIENT_ID
Mark every step as "run by the user". Do not execute any of them.