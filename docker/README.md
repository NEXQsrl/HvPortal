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
docker build --provenance=false --sbom=false --platform linux/amd64 -f docker/Dockerfile -t hvportal-gateway:0.9.4 .
```

Get the image onto the Docker host used by Portainer:
- push it to your private registry (`docker tag` + `docker push`) and set `HVP_IMAGE` to its name, **or**
- `docker save --platform linux/amd64 hvportal-gateway:0.9.4 -o hvportal-gateway-0.9.4.tar`, then Portainer > **Images > Import**.

NEXQ does not publish a prebuilt image: build it from the repository. The image contains `LICENSE` and `NOTICE` in
`/app` (PolyForm Shield License 1.0.0, see [docs/licensing.md](../docs/licensing.md)). If you share an image with
others, the license terms and the `Required Notice` line must go with it, and it must not be offered as a product that
competes with HvPortal. The full license texts of the third-party components are in `THIRD-PARTY-NOTICES.txt` of the
release of the same version; the image does not contain them yet.

The compose file uses `pull_policy: ${HVP_PULL_POLICY:-never}`: with an imported image Portainer does not try to
download it (otherwise: "pull access denied for hvportal-gateway, repository does not exist"). With a private
registry set `HVP_IMAGE` to the full name and `HVP_PULL_POLICY=missing` (or `always`).
The image is built for `linux/amd64`; for an ARM Docker host build it with `--platform linux/arm64`.
`--provenance=false --sbom=false` and `docker save --platform` produce a single-manifest archive that every Docker
engine (and Portainer's import) loads with its name; without them Docker Desktop writes a nested OCI index with
attestations that some engines import untagged ("No such image: hvportal-gateway:0.9.4" at deploy).

### "No such image" at deploy: checklist
1. Portainer > **Images** of the **same environment** as the stack: `hvportal-gateway:0.9.4` must be listed.
2. Listed as `<none>:<none>`? Open it and add the tag `hvportal-gateway:0.9.4` (or re-import the archive built as above).
3. From the Docker host shell: `docker load -i hvportal-gateway-0.9.4.tar` must print `Loaded image: hvportal-gateway:0.9.4`.

## 1b. Or let Docker build it (no image to import)

- **Portainer > Images > Build a new image > Upload**: upload `hvportal-source-<version>.tar` (made with
  `git archive --format=tar -o hvportal-source-0.9.4.tar HEAD`), Dockerfile path `docker/Dockerfile`, image name
  `hvportal-gateway:0.9.4`, **Build**. Then deploy `docker-compose.yml` as below.
- **Portainer > Stacks > Repository** (Git repository reachable by Portainer, read-only token for a private
  repository): compose path `docker/docker-compose.build.yml` - the image is built at deploy.
- **Docker host shell**, in the source folder: `docker compose -f docker/docker-compose.build.yml up -d --build`.

The build needs Internet access from the Docker host (base images, NuGet and npm packages).

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
