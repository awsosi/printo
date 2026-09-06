# Deploying Printo

Two halves, deployed independently: the **server**, as a Docker Compose stack behind Traefik,
and the **agent**, as an MSI pushed to workstations by Group Policy.

They are loosely coupled on purpose. An agent with no server routes on the rules it shipped
with; a server with no agents still runs the scan-to-print pipeline. Neither half has to be
deployed first.

---

## 1. Server

### 1.1 First run

```bash
cp .env.production.example .env
$EDITOR .env                       # every REPLACE- value must change
docker compose -f infra/docker-compose.prod.yml up -d --build
```

The stack refuses to start if a secret is still unset — the compose file declares them as
`${VAR:?set VAR}` — rather than coming up with a known-weak default.

Generate secrets with `openssl rand -hex 32`. **Hex, not base64.** `POSTGRES_PASSWORD` is
interpolated into a `postgres://user:password@db:5432/name` URL, so a password containing
`/ @ : + # ?` has to be percent-encoded or the API cannot parse its own connection string. Hex
sidesteps the question at the same 256 bits of entropy. (The API refuses to start with a clear
message if this is got wrong, rather than a bare `Invalid URL`.)

Verify a deployment end to end at any time:

```bash
npm run smoke:prod
```

That builds the real images and checks the console and the fleet API answer through the proxy,
HTTP redirects to HTTPS, migrations ran, an unenrolled agent is refused, and **only Traefik
publishes a port**.

### 1.2 What is exposed

| Path | Goes to | Used by |
|---|---|---|
| `/` | web | the admin console, in a browser |
| `/api/…` | api | the Windows agents, and the console's own calls |

Only Traefik publishes a port. The API, the worker, the vision service, Postgres and Redis are
reachable only on the internal Docker network — nothing on the LAN can address them directly.
Outbound *is* allowed, because the worker mounts SMB shares and opens raw sockets to printers.

### 1.3 TLS

Put `printo.crt` and `printo.key` in `infra/traefik/certs/`, then enable the store:

```bash
cp infra/traefik/dynamic/tls.yml.example infra/traefik/dynamic/tls.yml
docker compose -f infra/docker-compose.prod.yml restart traefik
```

With no certificate the stack still serves HTTPS, using one Traefik generates for itself.
Browsers warn, and **the agents will refuse to connect** until the issuing CA is trusted — which
for a domain-joined fleet means issuing this certificate from the same internal ADCS that signs
the agent MSI, so the chain is already trusted everywhere by GPO. See
`infra/traefik/certs/README.md`.

> Do not copy `tls.yml.example` into place before the two files exist. A default certificate
> naming files that are not there does not degrade gracefully: Traefik fails to build the
> certificate store and then *every* TLS handshake fails, so the stack looks healthy and serves
> nothing.

### 1.4 Routing is configured in files, never in labels

`infra/traefik/dynamic/` holds the routers, services and middlewares. There is no Docker
provider and no container labels. Labels put a service's routing inside the thing being routed,
so a container can silently re-route itself and `docker inspect` becomes the only way to answer
"what is published". Files can be read, reviewed and diffed.

Traefik watches that directory and reloads routing changes in place — except that the watch is
driven by inotify, which does not fire for a file created on a bind mount from a Windows or
macOS host. Restart the proxy after adding a file there.

### 1.5 Housekeeping

Retention runs daily inside the API, behind a Postgres advisory lock so a scaled-out deployment
sweeps once rather than once per replica. Windows are configured per data class in the admin
console. Set `RETENTION_INTERVAL_HOURS=0` to disable it and drive `POST /admin/retention/run`
from an external cron instead.

---

## 2. Agent

### 2.1 Build the MSI

```powershell
dotnet tool install --global wix --version 5.*
pwsh clients/windows/installer/build.ps1 -Version 0.1.0
```

Roughly 48 MB, containing a self-contained .NET runtime. Self-contained on purpose: a fleet is
much easier to keep correct when the MSI carries its own runtime than when every workstation
needs a matching .NET version deployed first, and one missing prerequisite is a packing bench
that cannot print.

### 2.2 Sign it

The certificate comes from the customer's internal ADCS and is not in this repository, so the
build produces an **unsigned** MSI by default.

```powershell
pwsh clients/windows/installer/build.ps1 -Version 0.1.0 -CertificateThumbprint <thumbprint>
```

That signs the two executables *and* the MSI. Signing only the MSI would leave the installed
binaries unsigned, and it is those that AV inspects every time the service starts.

An unsigned MSI installs perfectly well by GPO on a domain-joined machine. Signing is what
stops SmartScreen complaining when someone runs it by hand, and what lets the AV exclusions
below be scoped to a publisher rather than to a path.

To issue the certificate (on a domain-joined machine, as an operator who may enrol
certificates, using a template with the Code Signing EKU):

```powershell
$cert = Get-Certificate -Template CodeSigning -CertStoreLocation Cert:\CurrentUser\My
$cert.Certificate.Thumbprint
```

### 2.3 Verify the package before deploying it

Install, upgrade and uninstall cannot be proved by inspecting the MSI. On a **test machine or a
VM**, from an elevated PowerShell:

```powershell
pwsh clients/windows/installer/build.ps1 -Version 0.1.0
pwsh clients/windows/installer/build.ps1 -Version 0.1.1
pwsh clients/windows/installer/Verify-Install.ps1 `
    -Msi clients/windows/installer/bin/PrintoAgent-0.1.0.msi `
    -UpgradeMsi clients/windows/installer/bin/PrintoAgent-0.1.1.msi
```

It checks a clean install, that the service is registered, automatic and running, that the
unattended properties reached the agent, that the data directory excludes ordinary users, that
an in-place upgrade keeps the service running and keeps the site's data, and that uninstall
leaves no service, no install directory and no registry key.

### 2.4 Deploy by GPO

1. Copy the MSI to a share every machine account can read, e.g.
   `\\<domain>\NETLOGON\Printo\PrintoAgent-0.1.0.msi`.
2. Create a GPO linked to the OU holding the workstations.
3. **Computer Configuration → Policies → Software Settings → Software installation** → new
   package → *Assigned*. Machine-assigned, so it installs at boot without a user signing in.
4. Copy `clients/windows/installer/policy/Printo.admx` and `policy/en-US/Printo.adml` to the
   central store (`\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions\`).
5. Configure **Computer Configuration → Policies → Administrative Templates → Printo → Agent**.

Upgrades: build a higher version, put it on the share, and add it to the same GPO as an
*upgrade* of the existing package, replacing it. The MSI's `MajorUpgrade` removes the old
product after the new files are staged, so a machine that loses power mid-upgrade comes back
with one working version rather than none.

### 2.5 Configuration, and where each value comes from

Four layers, weakest first:

| Layer | Where | Set by |
|---|---|---|
| Default | compiled in | — |
| File | `%ProgramData%\Printo\agent\agent.json` | an administrator on the machine |
| Install | `HKLM\SOFTWARE\Printo\Agent` | the MSI's properties |
| Policy | `HKLM\SOFTWARE\Policies\Printo\Agent` | Group Policy |

Ask any machine what it resolved, and why:

```powershell
& "$env:ProgramFiles\Printo Agent\Printo.Agent.exe" --show-config
```

```
configuration file: C:\ProgramData\Printo\agent\agent.json (present)
  ServerUrl = https://printo.example.local/api/   [from Policy] (managed by Group Policy)
  DecisionMode = Auto   [from Install]
  ConfidenceThreshold = 0.75   [from Default]
  ...
```

The same lines go to the event log at every start, so the question is answerable without
reaching the workstation. A value pushed by policy is marked managed, and the tray renders it
read-only — an editable box that silently reverts at the next policy refresh is worse than no
box at all.

**Printers and watched folders are not policy-managed.** They are per-machine facts — this bench
has that thermal printer — and pushing them by GPO would mean one policy object per workstation,
which is not a policy, it is a spreadsheet. They live in `agent.json`, or are set per agent from
the fleet console.

### 2.6 Unattended enrolment

Issue a multi-use token in the console (**Fleet → Issue enrolment token**) and set it as the
**Enrolment token** policy. Each machine reads it once on first start, exchanges it for its own
per-machine credential, and never needs it again. Clear the policy once the fleet has enrolled.

The token is not a long-lived secret: it only permits enrolment, it expires, and its use count
is enforced server-side. The credential it buys is stored in `%ProgramData%\Printo\agent\` under
an ACL that grants only SYSTEM, Administrators and the account the agent runs as.

Alternatively pass it to the MSI directly, which is what a one-off install wants:

```
msiexec /i PrintoAgent-0.1.0.msi /qn ^
  SERVERURL=https://printo.example.local/api/ ^
  DECISIONMODE=auto ^
  ENROLLMENTTOKEN=<token>
```

If the server later rejects an agent's key — the machine was disabled, retired or re-imaged —
the agent drops the credential rather than retrying forever, so a freshly issued token is all
it takes to recover.

### 2.7 Antivirus exclusions

The agent renders PDFs, writes to a SQLite spool and talks to printer drivers. That behaviour
resembles enough malware to be worth excluding explicitly; without it, on-access scanning adds
latency to every page and some products quarantine `pdfium.dll` outright.

Exclude, on workstations only:

| What | Path |
|---|---|
| Process | `%ProgramFiles%\Printo Agent\Printo.Agent.exe` |
| Process | `%ProgramFiles%\Printo Agent\Printo.Tray.exe` |
| Folder | `%ProgramData%\Printo\agent\` |

The data folder matters most: the spool database is written on every job, and real-time scanning
of a SQLite file in use is both slow and a source of spurious locking.

Prefer publisher-based exclusions once the binaries are signed — a path exclusion protects
anything that gets written to that path, whereas a publisher exclusion follows the actual
software.

For Microsoft Defender, by GPO: **Computer Configuration → Policies → Administrative Templates →
Windows Components → Microsoft Defender Antivirus → Exclusions**.

### 2.8 What the agent needs on a workstation

- 64-bit Windows 10 22H2 or Windows 11. The MSI refuses to install on 32-bit.
- No .NET prerequisite: the runtime is in the package.
- Outbound HTTPS to the server, if one is configured.
- A Windows OCR language pack, if documents in a language other than the machine's own have to
  be read. Without OCR the agent asks an operator rather than guessing, so this reduces prompts;
  it does not affect correctness.

---

## 3. What is not yet verified

Stated plainly, because a deployment plan that overstates its evidence is worse than one with
gaps in it:

- **No physical printer has printed anything from this system.** Composition, placement and the
  GDI and ZPL paths are asserted against recorded output and reference images, and printable
  geometry is read from a real installed driver — but whether a given printer marks the stock
  where those numbers say needs the hardware session (plan §10.2).
- **The MSI has not been installed.** It is built and its contents are verified — service
  registration, upgrade sequencing, ACLs, registry values, 318 payload files, no debug symbols —
  but installing it needs elevation. `Verify-Install.ps1` is written for exactly that and has
  not been run.
- **Virtual-printer ingress is not built.** It is blocked on the M1 capture spike, which needs
  one elevated command to finish (plan §5.0). Hot folders work today.
