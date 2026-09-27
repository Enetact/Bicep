# Property-level review gates supplement ARM validation and platform Azure Policy.
function Assert-ServiceChange($Change) {
    if ($Change.changeType -in @('NoChange','NoEffect')) { return }
    if ($Change.changeType -eq 'Modify') {
        if (!$Change.Contains('delta') -or @($Change.delta).Count -eq 0) { throw "Unanalyzed Modify without property deltas: $($Change.resourceId)" }
        if ($Change.resourceId -match '/providers/(Microsoft.Authorization/roleAssignments|Microsoft.ManagedIdentity/userAssignedIdentities|Microsoft.Network/(privateEndpoints|privateDnsZones|virtualNetworks|networkSecurityGroups|routeTables|azureFirewalls|dnsResolvers))(/|$)') {
            throw "High-risk topology, identity or RBAC modification requires platform migration review: $($Change.resourceId)"
        }
        if ($Change.resourceId -match '/Microsoft.Web/sites/[^/]+/config/authsettingsV2$') { throw 'Authentication configuration changes require platform security review.' }
        $pending=[Collections.Generic.Queue[object]]::new()
        foreach ($entry in $Change.delta) { $pending.Enqueue(@{node=$entry;prefix=''}) }
        while ($pending.Count) {
            $item=$pending.Dequeue(); $node=$item.node
            if ($node -isnot [Collections.IDictionary] -or !$node.Contains('path') -or !$node.path -or !$node.Contains('propertyChangeType')) { throw 'Unanalyzed what-if property delta.' }
            if ($node.propertyChangeType -notin @('Create','Delete','Modify','Array','NoEffect')) { throw 'Unanalyzed what-if property change type.' }
            if ($node.propertyChangeType -eq 'NoEffect') { continue }
            $path="$($item.prefix)$($node.path)"
            if ($path -match '(?i)(^|\.)(identity|sku|location|publicNetworkAccess|allowBlobPublicAccess|allowSharedKeyAccess|minimumTlsVersion|disableLocalAuth|enableRbacAuthorization|enablePurgeProtection|enableSoftDelete|allowedAudiences|allowedClientApplications|requireAuthentication|httpsOnly|virtualNetworkSubnetId|subnet|networkAcls|ipSecurityRestrictions|scmIpSecurityRestrictions|roleDefinitionId|principalId|privateEndpointConnections|dnsSettings|siteConfig\.vnetRouteAllEnabled)(\.|\[|$)') {
                throw "High-risk property '$path' requires platform migration review: $($Change.resourceId)"
            }
            # A parent object replacement can hide a sensitive child from the delta path.
            foreach ($side in @('before','after')) {
                if ($node.Contains($side) -and $node[$side] -is [Collections.IDictionary]) {
                    if ((ConvertTo-Canonical $node[$side]) -match '"(identity|sku|location|publicNetworkAccess|allowBlobPublicAccess|allowSharedKeyAccess|minimumTlsVersion|disableLocalAuth|enableRbacAuthorization|enablePurgeProtection|enableSoftDelete|allowedAudiences|allowedClientApplications|requireAuthentication|httpsOnly|virtualNetworkSubnetId|subnet|networkAcls|roleDefinitionId|principalId)":') { throw "High-risk object replacement at '$path' requires platform review." }
                }
            }
            if ($node.Contains('children')) { foreach ($child in $node.children) { $pending.Enqueue(@{node=$child;prefix="$path."}) } }
        }
    }
    # Validate explicit create/modify payloads too; monitor public ingestion is a documented exception.
    if ($Change.Contains('after') -and $Change.after -is [Collections.IDictionary]) {
        $json=ConvertTo-Canonical $Change.after
        if ($Change.resourceId -match '/providers/(Microsoft.Storage/storageAccounts|Microsoft.Web/sites|Microsoft.Sql/servers|Microsoft.KeyVault/vaults|Microsoft.ServiceBus/namespaces)(/|$)' -and $json -match '"publicNetworkAccess":"Enabled"|"allowBlobPublicAccess":true|"allowSharedKeyAccess":true|"disableLocalAuth":false|"enableRbacAuthorization":false|"enablePurgeProtection":false|"requireAuthentication":false|"httpsOnly":false|"minimumTlsVersion":"(TLS1_0|TLS1_1|1\.0|1\.1)"') { throw "Insecure public access or transport in planned resource: $($Change.resourceId)" }
    }
}
