// Alerts on the signals the application emits (docs/operations/monitoring.md lists each one and what to do). Everything goes to one action group;
// add receivers (email, Teams webhook, pager) through the parameters.
param prefix string
param location string
param tags object
param workspaceId string
param sqlServerResourceId string
param emailReceivers array = []
param webhookReceivers array = []

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${prefix}-oncall'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'vpms-oncall'
    enabled: true
    emailReceivers: [for (r, i) in emailReceivers: { name: 'email-${i}', emailAddress: r, useCommonAlertSchema: true }]
    webhookReceivers: [for (r, i) in webhookReceivers: { name: 'hook-${i}', serviceUri: r, useCommonAlertSchema: true }]
  }
}

var rules = [
  {
    name: 'api-5xx'
    description: 'Server errors on the API above the threshold.'
    severity: 2
    window: 'PT10M'
    frequency: 'PT5M'
    threshold: 5
    query: 'AppRequests | where Success == false and toint(ResultCode) >= 500 | summarize Failures = count()'
    metric: 'Failures'
  }
  {
    name: 'api-latency'
    description: 'API p95 latency above 3 seconds.'
    severity: 3
    window: 'PT15M'
    frequency: 'PT5M'
    threshold: 3000
    query: 'AppRequests | summarize P95 = percentile(DurationMs, 95)'
    metric: 'P95'
  }
  {
    name: 'job-failures'
    description: 'Background job passes are failing (vpms.job.runs with outcome=failed).'
    severity: 2
    window: 'PT30M'
    frequency: 'PT10M'
    threshold: 3
    query: 'AppMetrics | where Name == "vpms.job.runs" and tostring(Properties["outcome"]) == "failed" | summarize Failed = sum(Sum)'
    metric: 'Failed'
  }
  {
    name: 'worker-silent'
    description: 'No background job pass has been reported for 20 minutes: the worker is down or stuck.'
    severity: 1
    window: 'PT20M'
    frequency: 'PT5M'
    threshold: 1
    operator: 'LessThan'
    query: 'AppMetrics | where Name == "vpms.job.runs" | summarize Runs = coalesce(sum(Sum), 0.0)'
    metric: 'Runs'
    noDataIsAlert: true
  }
  {
    name: 'security-events'
    description: 'Spike in security events (login failures, access denied, CSRF failures, rate limiting).'
    severity: 2
    window: 'PT15M'
    frequency: 'PT5M'
    threshold: 50
    query: 'AppMetrics | where Name == "vpms.security.events" | summarize Events = sum(Sum)'
    metric: 'Events'
  }
  {
    name: 'audit-chain-broken'
    description: 'The audit hash chain did not verify, or no longer matches the external anchors. Treat as a security incident (runbook: incident response, audit chain).'
    severity: 0
    window: 'PT1H'
    frequency: 'PT5M'
    threshold: 0
    query: 'ContainerAppConsoleLogs_CL | where Log_s has "AUDIT CHAIN BROKEN" or Log_s has "Events are missing" | summarize Hits = count()'
    metric: 'Hits'
  }
  {
    name: 'startup-refused'
    description: 'A container refused to start because of an unsafe Production configuration.'
    severity: 1
    window: 'PT30M'
    frequency: 'PT10M'
    threshold: 0
    query: 'ContainerAppConsoleLogs_CL | where Log_s has "Unsafe Production configuration" | summarize Hits = count()'
    metric: 'Hits'
  }
]

resource logRules 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = [for r in rules: {
  name: '${prefix}-${r.name}'
  location: location
  tags: tags
  properties: {
    displayName: '${prefix} ${r.name}'
    description: r.description
    severity: r.severity
    enabled: true
    scopes: [workspaceId]
    evaluationFrequency: r.frequency
    windowSize: r.window
    autoMitigate: true
    criteria: {
      allOf: [{
        query: r.query
        timeAggregation: 'Maximum'
        metricMeasureColumn: r.metric
        operator: r.?operator ?? 'GreaterThan'
        threshold: r.threshold
        failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
      }]
    }
    actions: { actionGroups: [actionGroup.id] }
  }
}]

resource sqlCpu 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-sql-cpu'
  location: 'global'
  tags: tags
  properties: {
    description: 'Database CPU above 85% for 15 minutes.'
    severity: 3
    enabled: true
    scopes: ['${sqlServerResourceId}/databases/Vpms']
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{ name: 'cpu', metricName: 'cpu_percent', operator: 'GreaterThan', threshold: 85, timeAggregation: 'Average', criterionType: 'StaticThresholdCriterion' }]
    }
    actions: [{ actionGroupId: actionGroup.id }]
  }
}

resource sqlStorage 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-sql-storage'
  location: 'global'
  tags: tags
  properties: {
    description: 'Database storage above 85% of the maximum size.'
    severity: 2
    enabled: true
    scopes: ['${sqlServerResourceId}/databases/Vpms']
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{ name: 'storage', metricName: 'storage_percent', operator: 'GreaterThan', threshold: 85, timeAggregation: 'Maximum', criterionType: 'StaticThresholdCriterion' }]
    }
    actions: [{ actionGroupId: actionGroup.id }]
  }
}

output actionGroupId string = actionGroup.id
