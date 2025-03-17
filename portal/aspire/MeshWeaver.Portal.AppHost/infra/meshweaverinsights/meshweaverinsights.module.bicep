@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param applicationType string = 'web'

param kind string = 'web'

param logAnalyticsWorkspaceId string

resource meshweaverinsights 'Microsoft.Insights/components@2020-02-02' = {
  name: take('meshweaverinsights-${uniqueString(resourceGroup().id)}', 260)
  kind: kind
  location: location
  properties: {
    Application_Type: applicationType
    WorkspaceResourceId: logAnalyticsWorkspaceId
  }
  tags: {
    'aspire-resource-name': 'meshweaverinsights'
  }
}

output appInsightsConnectionString string = meshweaverinsights.properties.ConnectionString