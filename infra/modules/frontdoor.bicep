// Optional edge: Azure Front Door Premium with a WAF in front of the web app. The web app only accepts requests that carry this profile's id
// (FRONT_DOOR_ID in the web container), so the container app's own address cannot be used to go around the WAF.
param prefix string
param tags object
param originHost string
@allowed(['Prevention', 'Detection'])
param wafMode string = 'Prevention'
param monitoringWorkspaceId string

resource profile 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: '${prefix}-fd'
  location: 'global'
  tags: tags
  sku: { name: 'Premium_AzureFrontDoor' }
}

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: profile
  name: '${prefix}-edge'
  location: 'global'
  tags: tags
  properties: { enabledState: 'Enabled' }
}

resource originGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: profile
  name: 'web'
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3 }
    healthProbeSettings: { probePath: '/healthz', probeRequestType: 'GET', probeProtocol: 'Https', probeIntervalInSeconds: 30 }
  }
}

resource origin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: originGroup
  name: 'web'
  properties: {
    hostName: originHost
    originHostHeader: originHost
    httpsPort: 443
    priority: 1
    weight: 1000
    enforceCertificateNameCheck: true
  }
}

resource waf 'Microsoft.Network/FrontDoorWebApplicationFirewallPolicies@2024-02-01' = {
  name: replace('${prefix}waf', '-', '')
  location: 'global'
  tags: tags
  sku: { name: 'Premium_AzureFrontDoor' }
  properties: {
    policySettings: { enabledState: 'Enabled', mode: wafMode, requestBodyCheck: 'Enabled' }
    managedRules: {
      managedRuleSets: [
        { ruleSetType: 'Microsoft_DefaultRuleSet', ruleSetVersion: '2.1', ruleSetAction: 'Block' }
        { ruleSetType: 'Microsoft_BotManagerRuleSet', ruleSetVersion: '1.1' }
      ]
    }
  }
}

resource security 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = {
  parent: profile
  name: 'waf'
  properties: {
    parameters: {
      type: 'WebApplicationFirewall'
      wafPolicy: { id: waf.id }
      associations: [{ domains: [{ id: endpoint.id }], patternsToMatch: ['/*'] }]
    }
  }
}

resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    originGroup: { id: originGroup.id }
    supportedProtocols: ['Https']
    httpsRedirect: 'Enabled'
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    patternsToMatch: ['/*']
    // The application sets its own cache headers; nothing is cached at the edge.
    cacheConfiguration: null
  }
  dependsOn: [origin]
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: profile
  name: 'to-logs'
  properties: {
    workspaceId: monitoringWorkspaceId
    logs: [
      { category: 'FrontDoorAccessLog', enabled: true }
      { category: 'FrontDoorWebApplicationFirewallLog', enabled: true }
    ]
  }
}

output hostName string = endpoint.properties.hostName
output frontDoorId string = profile.properties.frontDoorId
