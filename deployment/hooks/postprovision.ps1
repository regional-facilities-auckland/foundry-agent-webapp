#!/usr/bin/env pwsh
# Post-provision: Updates Entra app redirect URIs and assigns RBAC to AI Foundry resource

$ErrorActionPreference = "Stop"
. "$PSScriptRoot/modules/HookLogging.ps1"
Start-HookLog -HookName "postprovision" -EnvironmentName $env:AZURE_ENV_NAME

Write-Host "Post-Provision: Configure Entra App & RBAC" -ForegroundColor Cyan

# Get required env vars
$clientId = azd env get-value ENTRA_SPA_CLIENT_ID 2>$null
$containerAppUrl = azd env get-value WEB_ENDPOINT 2>$null
$webIdentityPrincipalId = azd env get-value WEB_IDENTITY_PRINCIPAL_ID 2>$null
$aiFoundryResourceGroup = azd env get-value AI_FOUNDRY_RESOURCE_GROUP 2>$null
$aiFoundryResourceName = azd env get-value AI_FOUNDRY_RESOURCE_NAME 2>$null
$aiAgentEndpoint = azd env get-value AI_AGENT_ENDPOINT 2>$null
$subscriptionId = azd env get-value AZURE_SUBSCRIPTION_ID 2>$null

if (-not $clientId) {
    Write-Host "[ERROR] ENTRA_SPA_CLIENT_ID not set" -ForegroundColor Red
    exit 1
}
if (-not $containerAppUrl) {
    Write-Host "[ERROR] WEB_ENDPOINT not set" -ForegroundColor Red
    exit 1
}

Write-Host "[OK] Container App: $containerAppUrl" -ForegroundColor Green

# Update Entra app redirect URIs
$app = az ad app show --id $clientId | ConvertFrom-Json
$redirectUris = @(
    "http://localhost:8080",
    "http://localhost:5173",
    $containerAppUrl
)

$spaBody = @{ spa = @{ redirectUris = $redirectUris } } | ConvertTo-Json -Depth 10
$tempFile = [System.IO.Path]::GetTempFileName()
$spaBody | Out-File -FilePath $tempFile -Encoding utf8

az rest --method PATCH `
    --uri "https://graph.microsoft.com/v1.0/applications/$($app.id)" `
    --headers "Content-Type=application/json" `
    --body "@$tempFile" | Out-Null

Remove-Item $tempFile -EA SilentlyContinue

if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Failed to update Entra app" -ForegroundColor Red
    exit 1
}

Write-Host "[OK] Redirect URIs updated" -ForegroundColor Green
$redirectUris | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }

function Ensure-RoleAssignment {
    param(
        [Parameter(Mandatory = $true)][string]$PrincipalObjectId,
        [Parameter(Mandatory = $true)][string]$RoleName,
        [Parameter(Mandatory = $true)][string]$Scope,
        [Parameter(Mandatory = $true)][string]$IdentityLabel
    )

    $existingAssignment = az role assignment list `
        --assignee $PrincipalObjectId `
        --role $RoleName `
        --scope $Scope 2>$null | ConvertFrom-Json

    if ($existingAssignment -and $existingAssignment.Count -gt 0) {
        Write-Host "[OK] $RoleName already assigned for $IdentityLabel" -ForegroundColor Green
        return
    }

    az role assignment create `
        --assignee-object-id $PrincipalObjectId `
        --assignee-principal-type ServicePrincipal `
        --role $RoleName `
        --scope $Scope | Out-Null

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[OK] Assigned $RoleName for $IdentityLabel" -ForegroundColor Green
    }
    else {
        Write-Host "[WARN] Failed to assign $RoleName for $IdentityLabel - you may need to do this manually" -ForegroundColor Yellow
    }
}

function Remove-AssignmentsUnderScope {
    param(
        [Parameter(Mandatory = $true)][string]$PrincipalObjectId,
        [Parameter(Mandatory = $true)][string]$ScopePrefix,
        [Parameter(Mandatory = $true)][string]$IdentityLabel
    )

    $assignments = az role assignment list --assignee-object-id $PrincipalObjectId --all 2>$null | ConvertFrom-Json
    if (-not $assignments) {
        return
    }

    foreach ($assignment in $assignments) {
        if ($assignment.scope -like "$ScopePrefix*") {
            az role assignment delete --ids $assignment.id | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "[OK] Removed $($assignment.roleDefinitionName) from $IdentityLabel" -ForegroundColor Green
            }
            else {
                Write-Host "[WARN] Failed to remove $($assignment.roleDefinitionName) from $IdentityLabel" -ForegroundColor Yellow
            }
        }
    }
}

$projectName = $null
if ($aiAgentEndpoint -and ($aiAgentEndpoint -match '/api/projects/([^/]+)')) {
    $projectName = $Matches[1]
}

$accountScope = $null
$projectScope = $null
if ($aiFoundryResourceGroup -and $aiFoundryResourceName -and $subscriptionId) {
    $accountScope = "/subscriptions/$subscriptionId/resourceGroups/$aiFoundryResourceGroup/providers/Microsoft.CognitiveServices/accounts/$aiFoundryResourceName"
    if ($projectName) {
        $projectScope = "$accountScope/projects/$projectName"
    }
}

# APIM endpoint mode: APIM managed identity is the single runtime caller to Foundry
$isApimEndpoint = $false
if ($aiAgentEndpoint) {
    try {
        $endpointUri = [Uri]$aiAgentEndpoint
        $isApimEndpoint = $endpointUri.Host.EndsWith(".azure-api.net")
    }
    catch {
        $isApimEndpoint = $false
    }
}

if ($isApimEndpoint -and $accountScope -and $projectScope) {
    $apimServiceName = ([Uri]$aiAgentEndpoint).Host.Split('.')[0]
    $apimResourceGroup = az apim list --query "[?name=='$apimServiceName'].resourceGroup | [0]" -o tsv 2>$null

    if ($apimResourceGroup) {
        $apimPrincipalId = az apim show -g $apimResourceGroup -n $apimServiceName --query identity.principalId -o tsv 2>$null
        if ($apimPrincipalId) {
            Write-Host "Assigning AI Foundry roles to APIM managed identity..." -ForegroundColor Yellow

            Ensure-RoleAssignment -PrincipalObjectId $apimPrincipalId -RoleName "Cognitive Services User" -Scope $accountScope -IdentityLabel "APIM managed identity"
            Ensure-RoleAssignment -PrincipalObjectId $apimPrincipalId -RoleName "Azure AI Project Manager" -Scope $projectScope -IdentityLabel "APIM managed identity"

            if ($webIdentityPrincipalId) {
                Write-Host "Removing overlapping Foundry roles from web app identity (APIM-only runtime model)..." -ForegroundColor Yellow
                Remove-AssignmentsUnderScope -PrincipalObjectId $webIdentityPrincipalId -ScopePrefix $accountScope -IdentityLabel "web app identity"
            }

            $spClientId = azd env get-value AZURE_CLIENT_ID 2>$null
            if ($spClientId) {
                $spObjectId = az ad sp show --id $spClientId --query id -o tsv 2>$null
                if ($spObjectId) {
                    Write-Host "Removing overlapping Foundry roles from service principal (APIM-only runtime model)..." -ForegroundColor Yellow
                    Remove-AssignmentsUnderScope -PrincipalObjectId $spObjectId -ScopePrefix $accountScope -IdentityLabel "service principal"
                }
            }
        }
        else {
            Write-Host "[WARN] APIM managed identity not found; skipped APIM RBAC assignment" -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "[WARN] APIM resource group not found for $apimServiceName; skipped APIM RBAC assignment" -ForegroundColor Yellow
    }
}
elseif ($accountScope) {
    Write-Host "[INFO] Non-APIM endpoint detected. Keeping legacy web/SP role assignment behavior." -ForegroundColor Gray

    if ($webIdentityPrincipalId) {
        Write-Host "Assigning AI Foundry roles to web app identity..." -ForegroundColor Yellow
        Ensure-RoleAssignment -PrincipalObjectId $webIdentityPrincipalId -RoleName "Cognitive Services User" -Scope $accountScope -IdentityLabel "web app identity"
        if ($projectScope) {
            Ensure-RoleAssignment -PrincipalObjectId $webIdentityPrincipalId -RoleName "Azure AI Project Manager" -Scope $projectScope -IdentityLabel "web app identity"
        }
    }

    $spClientId = azd env get-value AZURE_CLIENT_ID 2>$null
    if ($spClientId) {
        $spObjectId = az ad sp show --id $spClientId --query id -o tsv 2>$null
        if ($spObjectId) {
            Write-Host "Assigning AI Foundry roles to service principal..." -ForegroundColor Yellow
            Ensure-RoleAssignment -PrincipalObjectId $spObjectId -RoleName "Cognitive Services User" -Scope $accountScope -IdentityLabel "service principal"
            if ($projectScope) {
                Ensure-RoleAssignment -PrincipalObjectId $spObjectId -RoleName "Azure AI Project Manager" -Scope $projectScope -IdentityLabel "service principal"
            }
        }
    }
}
else {
    Write-Host "[SKIP] AI Foundry role assignment - missing scope configuration" -ForegroundColor Yellow
}

# Open browser
try { Start-Process $containerAppUrl } catch { }

Write-Host "[OK] Post-provision complete. URL: $containerAppUrl" -ForegroundColor Green

if ($script:HookLogFile) {
    Write-Host "[LOG] Log file: $script:HookLogFile" -ForegroundColor DarkGray
}
Stop-HookLog
