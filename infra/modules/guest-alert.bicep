// Guest signing key alert (ADR 0006 section 5.4): Azure Monitor scheduled-query alert on the backend log events
// GuestSigningKeyNearExpiry / GuestSigningKeyUnavailable / GuestSigningKeyRefreshFailing, plus an action group that emails the operator.
// The address is a deploy-time parameter (an address, not a secret); it is never committed.
param name string
param location string
param tags object
param appInsightsId string
@description('Operator email address for the action group.')
param alertEmail string

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-${name}-guestkey'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: take('gkey${replace(name, '-', '')}', 12)
    enabled: true
    emailReceivers: [
      { name: 'operator', emailAddress: alertEmail, useCommonAlertSchema: true }
    ]
  }
}

// The events are logged by the backend (ILogger -> Application Insights traces): NearExpiry/Unavailable at every key refresh (1 h)
// while the condition holds; RefreshFailing after 3 consecutive failed refreshes and on a startup prefill failure. 1 h window + 1 h frequency; the rule fires when at least one such trace exists.
var query = '''
traces
| where message has "GuestSigningKeyNearExpiry" or message has "GuestSigningKeyUnavailable" or message has "GuestSigningKeyRefreshFailing"
    or tostring(customDimensions.EventName) in ("GuestSigningKeyNearExpiry", "GuestSigningKeyUnavailable", "GuestSigningKeyRefreshFailing")
'''

resource alert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'alert-${name}-guest-signing-key'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: '${name}: guest signing key near expiry, unavailable or refresh failing'
    description: 'The backend logged GuestSigningKeyNearExpiry, GuestSigningKeyUnavailable or GuestSigningKeyRefreshFailing (refresh failed 3 times in a row, or the startup prefill failed; check Key Vault reachability and the Crypto User role). Rotate: az keyvault key rotate --vault-name <vault> --name guest-token-signing (needs Key Vault Crypto Officer). See docs/adr/0006-guest-access.md section 5.4.'
    severity: 2
    enabled: true
    scopes: [ appInsightsId ]
    evaluationFrequency: 'PT1H'
    windowSize: 'PT1H'
    autoMitigate: false
    criteria: {
      allOf: [
        {
          query: query
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    actions: {
      actionGroups: [ actionGroup.id ]
    }
  }
}

output actionGroupId string = actionGroup.id
