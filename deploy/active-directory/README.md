# Active Directory LDAPS connector

ESAR discovers AD computer objects through an authenticated **LDAPS** connection. The API service
runs the health check and the worker service runs the actual sync, so both containers need the
same DNS, routing, and certificate trust configuration.

## Requirements

- Use the DC's actual certificate FQDN, for example `dc01.esar.local`, not `172.16.0.4`.
  The LDAPS certificate must contain that FQDN in its CN or SAN and include Server Authentication
  EKU.
- Use private routing only: allow TCP 636 from the ESAR VM/private subnet to the DC. Do **not**
  open TCP 389 or 636 to the Internet or a public Azure NSG.
- Use a dedicated read-only AD service account. Its password is entered only in ESAR's UI and is
  encrypted at rest; never put it in `.env`, Compose files, Git, or command history.
- The account needs read access to the configured Base DN. The first test should target a small
  test OU and use a disabled connector so the scheduler cannot overlap the manual test.

## Private CA and Docker DNS overlays

If the DC certificate chains to a private AD/enterprise CA, create a PEM bundle containing the
public root CA and any intermediate CA certificates. Do not use a PFX or any private key.

```bash
sudo install -d -m 0755 /opt/esar/ldaps
sudo install -m 0444 /path/from-ca/ad-ldaps-ca-bundle.pem /opt/esar/ldaps/ad-ldaps-ca-bundle.pem
```

In the VM's existing `~/ESAR/.env` file, add only the public CA path:

```dotenv
AD_LDAPS_CA_FILE=/opt/esar/ldaps/ad-ldaps-ca-bundle.pem
```

The optional `docker-compose.ad-ldaps.yml` overlay makes that file available read-only to both
services and keeps OpenLDAP certificate verification at `demand`. It never disables TLS checking.

Prefer Azure VNet DNS that forwards/resolves through AD DNS. For a temporary single-DC test only,
also add the following values to `.env` and use `docker-compose.ad-hosts.yml`:

```dotenv
AD_DC_FQDN=dc01.esar.local
AD_DC_IP=172.16.0.4
```

The `extra_hosts` fallback is applied to both containers. It is not needed when Docker can already
resolve the DC name through VNet DNS.

## Deploy without changing ESAR data

The commands below rebuild only application images. They do not remove PostgreSQL, Redis,
RabbitMQ, reports, connectors, or asset volumes.

```bash
cd ~/ESAR
git pull --ff-only origin main

# Add -f docker-compose.ad-ldaps.yml only when the DC uses a private CA.
# Add -f docker-compose.ad-hosts.yml only when VNet/AD DNS is not available.
# This example assumes both optional overlays are needed; omit either -f line
# when its corresponding prerequisite is already satisfied.
docker compose -f docker-compose.yml -f docker-compose.prod.yml \
  -f docker-compose.ad-ldaps.yml -f docker-compose.ad-hosts.yml \
  --env-file .env config -q

docker compose -f docker-compose.yml -f docker-compose.prod.yml \
  -f docker-compose.ad-ldaps.yml -f docker-compose.ad-hosts.yml \
  --env-file .env up -d --build
```

If a public CA already signs the DC certificate, omit `docker-compose.ad-ldaps.yml`. If VNet/AD
DNS already works, omit `docker-compose.ad-hosts.yml` and its two variables.

## Preflight and first connector test

Before entering a password in ESAR, validate the TLS path from the VM:

```bash
openssl s_client -connect dc01.esar.local:636 -servername dc01.esar.local \
  -verify_hostname dc01.esar.local -verify_return_error \
  -CAfile /opt/esar/ldaps/ad-ldaps-ca-bundle.pem </dev/null
```

After deployment, confirm the same name resolves in both application containers and that the
native LDAP dependency is present:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env \
  exec -T esar-api getent ahostsv4 dc01.esar.local
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env \
  exec -T esar-workers getent ahostsv4 dc01.esar.local
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env \
  exec -T esar-api sh -c 'ldconfig -p | grep libldap'
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env \
  exec -T esar-workers sh -c 'ldconfig -p | grep libldap'
```

Create the connector disabled in the ESAR UI with these settings (replace the example values):

```text
server=dc01.esar.local
port=636
baseDn=OU=ESAR-Test,DC=esar,DC=local
username=svc_esar_ad@esar.local
password=<enter only in the UI>
useSsl=true
authType=Basic
timeoutSeconds=30
```

Run **Health check** first; it validates the bind and that the Base DN can be read without scanning
the directory. If that succeeds, enable the connector briefly and run one **Full sync**. Keep its
schedule disabled until that one job finishes successfully. AD discovery intentionally records
computer objects and AD metadata; IP/MAC enrichment requires a separate network/DHCP/endpoint
source.
