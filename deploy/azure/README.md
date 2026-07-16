# Azure trial deployment

Bu script mövcud Docker Compose stack-ini bir Ubuntu VM-də qaldırır. Yalnız test üçündür.

## Tələblər

* Azure CLI quraşdırılmış olmalıdır.
* VM-in klonlaya bildiyi Git repository HTTPS ünvanı.
* Sizin public IPv4 ünvanınız CIDR formatında, adətən `IP/32`.

## İşə salmaq

```powershell
.\deploy\azure\deploy-vm.ps1 `
  -RepositoryUrl "https://github.com/ORG/REPOSITORY.git" `
  -RepositoryBranch "main" `
  -AllowedIpCidr "YOUR_PUBLIC_IP/32"
```

Standart region `uksouth`-dur. Subscription həmin regionu qəbul etməzsə,
Azure Portal-da trial üçün əlçatan region seçib `-Location "REGION"` əlavə edin.

Nəticə URL-ləri və ilkin admin parolu `esar-azure-deployment.json` faylında saxlanılır. Bu faylı gizli saxlayın.

## Silmək

```powershell
az group delete --name rg-esar-trial --yes --no-wait
```
