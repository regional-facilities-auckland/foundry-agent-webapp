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

# Assign Cognitive Services User role to web managed identity on AI Foundry resource
# This is done via Azure CLI (not Bicep) to prevent azd from tracking the external resource group
if ($webIdentityPrincipalId -and $aiFoundryResourceGroup -and $aiFoundryResourceName -and $subscriptionId) {
    Write-Host "Assigning Cognitive Services User role to web app identity..." -ForegroundColor Yellow
    
    $scope = "/subscriptions/$subscriptionId/resourceGroups/$aiFoundryResourceGroup/providers/Microsoft.CognitiveServices/accounts/$aiFoundryResourceName"
    
    # Check if role assignment already exists
    $existingAssignment = az role assignment list `
        --assignee $webIdentityPrincipalId `
        --role "Cognitive Services User" `
        --scope $scope 2>$null | ConvertFrom-Json
    
    if ($existingAssignment -and $existingAssignment.Count -gt 0) {
        Write-Host "[OK] Role assignment already exists for web app identity" -ForegroundColor Green
    } else {
        az role assignment create `
            --assignee-object-id $webIdentityPrincipalId `
            --assignee-principal-type ServicePrincipal `
            --role "Cognitive Services User" `
            --scope $scope | Out-Null
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[OK] Role assignment created for web app identity" -ForegroundColor Green
        } else {
            Write-Host "[WARN] Failed to create role assignment - you may need to do this manually" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "[SKIP] AI Foundry role assignment for web identity - missing configuration" -ForegroundColor Yellow
    Write-Host "  Set AI_FOUNDRY_RESOURCE_GROUP and AI_FOUNDRY_RESOURCE_NAME environment variables" -ForegroundColor Gray
}

# Assign Cognitive Services User role to service principal (for local development)
# The service principal is configured via AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_CLIENT_SECRET in .env.local
$spClientId = azd env get-value AZURE_CLIENT_ID 2>$null
if ($spClientId -and $aiFoundryResourceGroup -and $aiFoundryResourceName -and $subscriptionId) {
    Write-Host "Assigning Cognitive Services User role to service principal..." -ForegroundColor Yellow
    
    # Resolve the service principal object ID from client ID
    $spObjectId = az ad sp show --id $spClientId --query id -o tsv 2>$null
    
    if ($spObjectId) {
        $scope = "/subscriptions/$subscriptionId/resourceGroups/$aiFoundryResourceGroup/providers/Microsoft.CognitiveServices/accounts/$aiFoundryResourceName"
        
        # Check if role assignment already exists
        $existingAssignment = az role assignment list `
            --assignee $spObjectId `
            --role "Cognitive Services User" `
            --scope $scope 2>$null | ConvertFrom-Json
        
        if ($existingAssignment -and $existingAssignment.Count -gt 0) {
            Write-Host "[OK] Role assignment already exists for service principal" -ForegroundColor Green
        } else {
            az role assignment create `
                --assignee-object-id $spObjectId `
                --assignee-principal-type ServicePrincipal `
                --role "Cognitive Services User" `
                --scope $scope | Out-Null
            
            if ($LASTEXITCODE -eq 0) {
                Write-Host "[OK] Role assignment created for service principal" -ForegroundColor Green
            } else {
                Write-Host "[WARN] Failed to create role assignment for service principal - you may need to do this manually" -ForegroundColor Yellow
            }
        }
    } else {
        Write-Host "[WARN] Service principal $spClientId not found in Entra ID" -ForegroundColor Yellow
    }
} else {
    if (-not $spClientId) {
        Write-Host "[SKIP] Service principal role assignment - AZURE_CLIENT_ID not set" -ForegroundColor Gray
    }
}

# Open browser
try { Start-Process $containerAppUrl } catch { }

Write-Host "[OK] Post-provision complete. URL: $containerAppUrl" -ForegroundColor Green

if ($script:HookLogFile) {
    Write-Host "[LOG] Log file: $script:HookLogFile" -ForegroundColor DarkGray
}
Stop-HookLog
