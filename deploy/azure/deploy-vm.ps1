<#
Deploys ESAR to a single Ubuntu VM for a short-lived Azure trial environment.

Example:
  .\deploy\azure\deploy-vm.ps1 -RepositoryUrl "https://github.com/ORG/REPOSITORY.git" -AllowedIpCidr "203.0.113.10/32"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RepositoryUrl,
    [Parameter(Mandatory)] [string] $AllowedIpCidr,
    [string] $RepositoryBranch = "main",
    [string] $SubscriptionId,
    [string] $Location = "uksouth",
    [string] $ResourceGroup = "rg-esar-trial",
    [string] $VmName = "vm-esar-trial",
    [string] $AdminUsername = "azureuser",
    [string] $VmSize = "Standard_B2s"
)

$ErrorActionPreference = "Stop"

function Require-Command([string] $Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name tapılmadı. Azure CLI-ni quraşdırın: https://learn.microsoft.com/cli/azure/install-azure-cli-windows"
    }
}

function New-Secret([int] $Length = 36) {
    $alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#%_-"
    -join (1..$Length | ForEach-Object { $alphabet[(Get-Random -Maximum $alphabet.Length)] })
}

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Arguments)
    & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI əmri uğursuz oldu: az $($Arguments -join ' ')"
    }
}

Require-Command az
if (-not (az account show 2>$null)) { Invoke-Az login }
if ($SubscriptionId) { Invoke-Az account set --subscription $SubscriptionId }

$subscription = Invoke-Az account show --query id --output tsv
if (-not $subscription) { throw "Aktiv Azure subscription seçilməyib." }

$nsgName = "nsg-$VmName"
$postgresPassword = New-Secret
$rabbitPassword = New-Secret
$jwtKey = New-Secret 48
$adminPassword = New-Secret
$encryptionKeyBytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($encryptionKeyBytes)
$encryptionKey = [Convert]::ToBase64String($encryptionKeyBytes)

Write-Host "Resource group yaradılır..." -ForegroundColor Cyan
Invoke-Az group create --name $ResourceGroup --location $Location --output none

Write-Host "Şəbəkə qaydaları yaradılır..." -ForegroundColor Cyan
Invoke-Az network nsg create --resource-group $ResourceGroup --name $nsgName --location $Location --output none
Invoke-Az network nsg rule create --resource-group $ResourceGroup --nsg-name $nsgName --name AllowSshFromAdmin --priority 1000 --direction Inbound --access Allow --protocol Tcp --source-address-prefixes $AllowedIpCidr --destination-port-ranges 22 --output none
Invoke-Az network nsg rule create --resource-group $ResourceGroup --nsg-name $nsgName --name AllowPortalFromAdmin --priority 1010 --direction Inbound --access Allow --protocol Tcp --source-address-prefixes $AllowedIpCidr --destination-port-ranges 8090 --output none

Write-Host "Ubuntu VM yaradılır (bu bir neçə dəqiqə çəkə bilər)..." -ForegroundColor Cyan
Invoke-Az vm create --resource-group $ResourceGroup --name $VmName --image Ubuntu2204 --size $VmSize --admin-username $AdminUsername --generate-ssh-keys --nsg $nsgName --public-ip-sku Standard --output none

$remoteScript = @"
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y docker.io docker-compose-v2 git
systemctl enable --now docker
mkdir -p /opt/esar
if [ -d /opt/esar/.git ]; then
  git -C /opt/esar fetch origin '$RepositoryBranch'
  git -C /opt/esar checkout '$RepositoryBranch'
  git -C /opt/esar reset --hard 'origin/$RepositoryBranch'
else
  git clone --branch '$RepositoryBranch' --depth 1 '$RepositoryUrl' /opt/esar
fi
cat > /opt/esar/.env <<'EOF'
POSTGRES_PASSWORD=$postgresPassword
RABBITMQ_PASSWORD=$rabbitPassword
JWT_SIGNING_KEY=$jwtKey
ENCRYPTION_KEY=$encryptionKey
ADMIN_PASSWORD=$adminPassword
ASPNETCORE_ENVIRONMENT=Production
EOF
cd /opt/esar
docker compose up -d --build
docker compose ps
"@

$temporaryScript = New-TemporaryFile
try {
    Set-Content -LiteralPath $temporaryScript -Value $remoteScript -NoNewline
    Write-Host "Docker və ESAR quraşdırılır..." -ForegroundColor Cyan
    Invoke-Az vm run-command invoke --resource-group $ResourceGroup --name $VmName --command-id RunShellScript --scripts "@$temporaryScript" --output none
}
finally {
    Remove-Item -LiteralPath $temporaryScript -Force -ErrorAction SilentlyContinue
}

$publicIp = Invoke-Az vm show -d --resource-group $ResourceGroup --name $VmName --query publicIps --output tsv
$result = [ordered]@{
    subscriptionId = $subscription
    resourceGroup = $ResourceGroup
    vmName = $VmName
    portalUrl = "http://$publicIp`:8090"
    swaggerUrl = "http://$publicIp`:8090/swagger"
    initialAdminPassword = $adminPassword
    cleanupCommand = "az group delete --name $ResourceGroup --yes --no-wait"
}

$result | ConvertTo-Json | Set-Content -LiteralPath "./esar-azure-deployment.json"
Write-Host "Deployment tamamlandı." -ForegroundColor Green
$result | ConvertTo-Json
Write-Host "Nəticə və ilkin admin parolu esar-azure-deployment.json faylında saxlanıldı. Bu faylı Git-ə əlavə etməyin." -ForegroundColor Yellow
