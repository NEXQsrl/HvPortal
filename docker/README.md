# HvPortal gateway on Docker (Portainer)

The **gateway** (web console, REST API, agent channel) runs in a Linux container. The **agent** stays a Windows
service on every Hyper-V host and connects to the container (outbound from the host, mutual TLS).

```
browsers ──HTTPS:HVP_UI_PORT──►  ┌──────────────────────┐
                                  │ hvportal-gateway     │  volume hvportal-data:/data
Hyper-V hosts (agent) ──mTLS:HVP_AGENT_PORT──► (Linux) │  (DB, key ring, CA, Data Protection keys)
                                  └──────────────────────┘
```

## 1. Build the image

From the repository root (it builds the UI, the gateway and the Windows agent package served by the portal):

```bash
docker build -f docker/Dockerfile -t hvportal-gateway:0.3.4 .
```

Get the image onto the Docker host used by Portainer:
- push it to your private registry (`docker tag` + `docker push`) and set `HVP_IMAGE` to its name, **or**
- `docker save hvportal-gateway:0.3.4 -o hvportal-gateway-0.3.4.tar`, then Portainer > **Images > Import**.

The repository is private: do not push the image to a public registry.

## 2. Create the stack in Portainer

1. **Stacks > Add stack > Web editor**: paste `docker/docker-compose.yml`.
2. **Environment variables > Load variables from .env file**: load `docker/.env.example`, then set at least:
   - `HVP_PUBLIC_NAMES`: DNS names / IPs of the Docker host as the Hyper-V hosts see it (comma separated);
   - `HVP_KEY_ENCRYPTION_KEY`: `openssl rand -base64 32` — **save it in your password vault**.
3. **Deploy the stack**. The container becomes *healthy* in ~30 s.
4. **Containers > hvportal-gateway > Logs**: copy the line `HvPortal setup token ...`.
5. Open `https://<docker-host>:<HVP_UI_PORT>`, enter the token, create the administrator, enroll MFA.
6. Set `HVP_PRINT_SETUP_TOKEN=false` and update the stack. Then **Settings > Backup and restore > Create backup**.
7. Add the Hyper-V hosts from the portal (Inventory > Add host): the agent package is downloaded from the portal
   and the install command already contains a gateway name from `HVP_PUBLIC_NAMES` and the agent port.

## Network

| Port | From | Notes |
|---|---|---|
| `HVP_UI_PORT` (8443) | administrators' browsers | HTTPS. Behind a reverse proxy only with TLS pass-through or re-encryption to the same port and host name |
| `HVP_AGENT_PORT` (8444) | Hyper-V hosts only | **mutual TLS: never through a TLS-terminating proxy**; restrict it with the firewall to the host subnet |

The container listens on the same port numbers it publishes: the portal shows them in the agent commands.

## Security of the container

- Non-root user (uid 1654), read-only root file system, all Linux capabilities dropped, `no-new-privileges`.
- `/data` is private (directories 0700, files 0600). The key ring is encrypted with the KEK (`HVP_KEY_ENCRYPTION_KEY`
  or `HVP_KEY_ENCRYPTION_KEY_FILE` for a Docker secret); the ASP.NET Data Protection keys are encrypted with the key
  ring. Without a KEK the gateway generates `keys/kek.bin` inside the volume and logs a warning: acceptable for a
  lab only (anyone who can read the volume can then read the keys).
- Changing the KEK makes the existing volume unreadable. To move to a new KEK or a new server: **Settings > Backup**
  on the old one, then deploy the new one and **Settings > Backup and restore > Restore** (the restore re-encrypts
  the keys with the new KEK and is applied when the container restarts: "Restart now" does it).
- Migration from a Windows gateway: same procedure (backup on Windows, restore in the container). A Windows key ring
  (DPAPI) cannot be opened directly in the container.
- Telemetry: none. Outbound traffic only for Microsoft Entra ID when it is enabled.

## Certificate of the web console

By default the internal CA certificate is used (browsers warn; the CA can be downloaded from
`https://<host>:<HVP_AGENT_PORT>/agent/v1/ca` and trusted). For a certificate from your PKI, put the files in
`HVP_CERTS_DIR` (mounted read-only at `/certs`) and set `HVP_UI_CERT_PATH` (`/certs/portal.pfx` + `HVP_UI_CERT_PASSWORD`,
or `/certs/portal.crt` + `HVP_UI_CERT_KEY_PATH=/certs/portal.key`). In Portainer use an absolute host path.

## Update

Build/import the new image, change `HVP_IMAGE`, **Update the stack**. Database migrations run at start. Make a
backup first (Settings > Backup and restore).

## Administration commands

```bash
docker exec -it hvportal-gateway dotnet /app/HvPortal.Gateway.dll admin recover-account --user admin --reset-mfa
docker exec -it hvportal-gateway dotnet /app/HvPortal.Gateway.dll admin verify-audit
```
