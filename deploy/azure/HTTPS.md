# HTTPS deployment on the Azure VM

This production overlay adds Caddy as the only public-facing service. It routes the portal, `/api/*`, and `/swagger/*`; PostgreSQL, Redis, RabbitMQ, the API, and the frontend remain private in Docker.

## Azure prerequisites

1. Start the VM if it is deallocated.
2. In **Public IP address > Configuration**, confirm the DNS name is `esar.westus.cloudapp.azure.com` and use a static public IP.
3. In the VM NIC/subnet NSG, allow inbound TCP **80** and **443** from `Internet`. Port 80 is required for the normal Let's Encrypt HTTP-01 validation and HTTP-to-HTTPS redirect.
4. If UFW is enabled on Ubuntu, allow the same ports:

   ```bash
   sudo ufw allow 80/tcp
   sudo ufw allow 443/tcp
   sudo ufw status
   ```

Do not expose port 8080, 8090, 5432, 5672, 6379, or 15672 publicly. Remove old NSG rules for those ports after the HTTPS endpoint is verified.

## Deploy without losing existing data

The existing `.env` contains secrets that protect connector configuration. Do not replace it. Add only this non-secret value:

```env
ESAR_DOMAIN=esar.westus.cloudapp.azure.com
```

Then run on the VM:

```bash
cd ~/ESAR
git pull --ff-only origin main
nano .env
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env config
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env up -d --build
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env ps
docker logs esar-caddy-1 --tail 100
```

Use `up -d --build`, not `down -v`: named volumes retain PostgreSQL, Redis, RabbitMQ, reports, existing connectors, and assets.

## Validate

```bash
curl -I http://esar.westus.cloudapp.azure.com
curl -Iv https://esar.westus.cloudapp.azure.com
```

The first command should redirect to HTTPS. The second should show a valid certificate and a successful HTTP response. The portal URL is:

```
https://esar.westus.cloudapp.azure.com/
```

If certificate issuance fails, verify public DNS resolution and Internet reachability of port 80. Do not delete the `caddy-data` volume: it stores certificates and renewal state.

## Local/private fallback

Without `ESAR_DOMAIN`, Caddy serves HTTP only on port 80. This mode is for local or private-network testing, not an Internet-facing login portal.
