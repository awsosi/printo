# Deploying Printo

Two halves, deployed independently: the **server**, as a Docker Compose stack behind Traefik,
and the **agent**, as an MSI pushed to workstations by Group Policy — or, for the machines where
Windows Installer will not have it, as an EXE that installs the same product without msiexec.

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

### 2.1 Build the packages

```powershell
dotnet tool install --global wix --version 5.*
pwsh clients/windows/installer/build.ps1 -Version 0.1.0
```

That produces two installers from one publish:

| Package | Size | For |
|---|---|---|
| `PrintoAgent-0.1.0.msi` | ~57 MB | the fleet, deployed by Group Policy |
| `PrintoAgent-0.1.0.exe` | ~82 MB | machines the MSI cannot reach, and installs by hand |

Both carry a self-contained .NET runtime. Self-contained on purpose: a fleet is much easier to
keep correct when the package carries its own runtime than when every workstation needs a
matching .NET version deployed first, and one missing prerequisite is a packing bench that
cannot print.

**Why there are two.** Group Policy software installation accepts nothing but an MSI, so the MSI
is what a fleet is deployed with. But Windows Installer does not work on every machine: one
workstation refused this package for a day, and it turned out to be refusing *every* package —
a signed MSI from another vendor, and a file name that did not exist — with three lines of log
and 1603 before msiexec read a single property. A bootstrapper would not have helped, because a
bootstrapper ends in a call to msiexec. The EXE does the install itself, against the service
control manager and the registry, and never involves Windows Installer at all.

The EXE is the larger of the two by about 25 MB: the MSI's cab uses LZX where the EXE's embedded
payload is a deflate zip, and the EXE additionally carries the installer program itself (~11 MB).
It also leaves a copy of itself in the install directory, which is what Add/Remove Programs runs
to uninstall — so the installed footprint is ~11 MB larger too.

Build just one:

```powershell
pwsh clients/windows/installer/build.ps1 -Version 0.1.0 -Package Exe   # needs no WiX
pwsh clients/windows/installer/build.ps1 -Version 0.1.0 -Package Msi
```

### 2.2 Sign them

The certificate comes from the customer's internal ADCS and is not in this repository, so the
build produces **unsigned** packages by default.

```powershell
pwsh clients/windows/installer/build.ps1 -Version 0.1.0 -CertificateThumbprint <thumbprint>
```

That signs the two agent executables *and* each finished package. Signing only the package would
leave the installed binaries unsigned, and it is those that AV inspects every time the service
starts.

An unsigned MSI installs perfectly well by GPO on a domain-joined machine. Signing is what stops
SmartScreen complaining when someone runs it by hand — which matters rather more for the EXE,
since running it by hand is what it is for — and what lets the AV exclusions below be scoped to
a publisher rather than to a path.

To issue the certificate (on a domain-joined machine, as an operator who may enrol
certificates, using a template with the Code Signing EKU):

```powershell
$cert = Get-Certificate -Template CodeSigning -CertStoreLocation Cert:\CurrentUser\My
$cert.Certificate.Thumbprint
```

### 2.3 Verify a package before deploying it

Install, upgrade and uninstall cannot be proved by inspecting a package. On a **test machine or
a VM**, from an elevated PowerShell:

```powershell
pwsh clients/windows/installer/build.ps1 -Version 0.1.0
pwsh clients/windows/installer/build.ps1 -Version 0.1.1
pwsh clients/windows/installer/Verify-Install.ps1 `
    -Package clients/windows/installer/bin/PrintoAgent-0.1.0.msi `
    -UpgradePackage clients/windows/installer/bin/PrintoAgent-0.1.1.msi
```

It checks a clean install, that the service is registered, automatic and running, that the
virtual printer appears and captures a printed page, that the unattended settings reached the
agent, that the data directory excludes ordinary users, that an in-place upgrade keeps the
service running and keeps the site's data, and that uninstall leaves no service, no install
directory, no registry key and no Add/Remove Programs entry.

**Run it against both packages.** It takes either — pass the `.exe` in place of the `.msi` — and
the checks are the contract the two share. A machine installed by one has to be upgradable and
removable by the other, and running the same checks against each is what stops them drifting
apart. What can be proved without a machine is proved by `SetupParityTests`, which compares what
each package declares: the same service name, the same registry values under the same key, the
same autostart entry and the same unattended settings.

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

### 2.4a Install with the EXE

For a workstation the MSI cannot reach, and for installing by hand. It needs nothing installed
first and nothing repaired first.

Double-click it: it asks Windows for administrator rights, then prints what it is doing, step by
step, and stays on screen at the end. An install and a failure look nothing alike, which was not
true of the MSI until it was given a user interface.

Unattended, from an already-elevated prompt:

```powershell
.\PrintoAgent-0.1.0.exe /quiet `
    SERVERURL=https://printo.example.local/api/ `
    DECISIONMODE=auto `
    ENROLLMENTTOKEN=<token>
```

The settings are the MSI's properties, spelled the same and meaning the same, so an existing
install command carries over unchanged. A setting left out is left alone — an upgrade run with
no settings cannot blank a working machine's configuration. A **misspelt** setting stops the
install and says so, rather than being ignored.

| Switch | |
|---|---|
| `/quiet` | no prompts and no pause. Must be started already elevated: an unattended run must not stop at a consent dialog nobody is there to answer. |
| `/uninstall` | remove it. Also what Add/Remove Programs runs. |
| `/force` | install even though this exact version already is. Without it, that does nothing. |
| `/dir <path>` | somewhere other than `%ProgramFiles%\Printo Agent`. |
| `/log <path>` | the transcript, which is written by default to `%TEMP%\printo-setup-<stamp>.log`. |
| `/keep-printer` | on uninstall, leave the Windows print queue alone. |

Exit codes: `0` done, `1` a step failed (the transcript says which), `2` the command line could
not be read, `5` it needs an administrator and did not have one, `6` this machine cannot run it.

Upgrades: run the higher version. It stops the service, replaces the files it installed, removes
the ones the new version no longer ships, and starts the service again; the data directory and
the machine's configuration are untouched. It refuses to go backwards.

**Group Policy cannot deploy an EXE** through Software Installation — that accepts MSIs only.
Push it with a machine startup script, or with whatever management agent the site already has:

```powershell
# Computer Configuration → Policies → Windows Settings → Scripts → Startup
\\<domain>\NETLOGON\Printo\PrintoAgent-0.1.0.exe /quiet SERVERURL=https://printo.example.local/api/
```

A startup script runs as the machine, so it is already elevated. It also runs on every boot, and
the installer is built for that: a version that is already installed is reported and left alone,
a lower one is refused, and only a higher one does any work. Nothing is rewritten and the
service is not bounced on a bench that is already correct. `/force` overrides that, and is the
repair path for a machine somebody has deleted a file from.

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

### 2.5a The virtual printer

A workstation that has the agent installed presents a printer called **Printo**. Anything printed
to it is captured, routed page by page, and sent on to the physical printers this machine is
mapped to — the replacement for pointing Print&Share at a queue.

The agent creates and maintains that queue itself: at startup, and every fifteen minutes after,
it checks the queue exists and points at its own endpoint, and creates or repairs it if not. A
printer somebody deleted comes back on its own. The endpoint is IPP on **127.0.0.1:39631** and
is reachable only from the workstation, so there is no firewall rule to add.

| Setting | Default | Policy value |
|---|---|---|
| Present a virtual printer | on | `VirtualPrinterEnabled` |
| Queue name | `Printo` | `VirtualPrinterName` |
| Loopback port | `39631` | `VirtualPrinterPort` |
| Agent maintains the queue | on | `VirtualPrinterManageQueue` |

Turn **Manage the Windows queue** off at sites that deploy printers by Group Policy and do not
want an agent creating one locally. The endpoint still listens; create the queue yourself, while
the agent service is running, with:

```powershell
Add-Printer -Name Printo -IppURL http://127.0.0.1:39631/ipp/print
```

Windows reads the printer's capabilities before it will bind a queue, which is why the service
has to be running for that command — and why the agent, not the installer, owns this step.

Two commands for a machine that needs it done by hand:

```powershell
& "$env:ProgramFiles\Printo Agent\Printo.Agent.exe" --install-virtual-printer
& "$env:ProgramFiles\Printo Agent\Printo.Agent.exe" --remove-virtual-printer
```

Both installers run the second one for you when they remove the agent, and both ignore its
failure deliberately: a package that cannot be uninstalled would be a far worse outcome than a
printer left behind. In the MSI it is the package's only custom action and it runs on uninstall
only; the EXE takes `/keep-printer` to skip it, for a site that created the queue itself.

**What the queue offers applications.** PDF only, A4 and Letter plus whatever label sizes this
machine's thermal printers are configured for, and colour by default — the capture path is the
only chance the product gets at the original, and a page the client has already reduced to grey
cannot be recovered. Anything that arrives in another page description language is refused with
the format named, and the person who pressed print sees it fail in the Windows queue rather than
losing the job silently.

**If the port is taken.** The agent logs the failure loudly, keeps running its watched folders,
and finishes whatever is already in its spool. Set `VirtualPrinterPort` to something free; the
queue is recreated against the new port at the next start.

### 2.6 Unattended enrolment

Issue a multi-use token in the console (**Fleet → Issue enrolment token**) and set it as the
**Enrolment token** policy. Each machine reads it once on first start, exchanges it for its own
per-machine credential, and never needs it again. Clear the policy once the fleet has enrolled.

The token is not a long-lived secret: it only permits enrolment, it expires, and its use count
is enforced server-side. The credential it buys is written to
`%ProgramData%\Printo\agent\identity.json`, and the agent locks that file as it writes it: the
inherited rules are dropped and only SYSTEM, the administrators group and the account the agent
runs as are left.

The directory around it is deliberately less strict - SYSTEM and administrators in full,
ordinary users read and write. The tray runs as the signed-in operator and has to read this
machine's configuration, the document the picker is asking about, and the queue behind its
tooltip; a directory locked to administrators reads better in a review and leaves an operator
with a settings window that will not open. The secret is protected where the secret is.

Alternatively pass it to the installer directly, which is what a one-off install wants. The
settings are spelled the same either way:

```
msiexec /i PrintoAgent-0.1.0.msi /qn ^
  SERVERURL=https://printo.example.local/api/ ^
  DECISIONMODE=auto ^
  ENROLLMENTTOKEN=<token>

PrintoAgent-0.1.0.exe /quiet ^
  SERVERURL=https://printo.example.local/api/ ^
  DECISIONMODE=auto ^
  ENROLLMENTTOKEN=<token>
```

Neither writes the token anywhere a log will pick it up: the MSI marks the property hidden, and
the EXE keeps it out of its transcript.

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

Some endpoint products also inspect loopback traffic. The agent's virtual printer is an HTTP
listener on 127.0.0.1, and a product that intercepts it will stop print jobs reaching the agent;
exclude the port (39631 by default) if capture works with protection off and not with it on.

The data folder matters most: the spool database is written on every job, and real-time scanning
of a SQLite file in use is both slow and a source of spurious locking.

Prefer publisher-based exclusions once the binaries are signed — a path exclusion protects
anything that gets written to that path, whereas a publisher exclusion follows the actual
software.

For Microsoft Defender, by GPO: **Computer Configuration → Policies → Administrative Templates →
Windows Components → Microsoft Defender Antivirus → Exclusions**.

### 2.7a When an install fails

**The EXE says so itself.** It prints each step and its outcome, keeps the same transcript in
`%TEMP%\printo-setup-<stamp>.log`, and leaves the window open when it was double-clicked. There
is no separate logging switch to remember and no return code to look up. If it will not install,
the reason is the last line printed.

**The MSI is the one that needs a log.** Double-clicking it shows a short welcome, a progress
bar, and a page saying whether it worked; for the detail, or when deploying unattended, install
with a log:

```powershell
msiexec /i PrintoAgent-0.1.4.msi /l*v "$env:TEMP\printo-install.log"
```

Then find the action that failed - Windows Installer marks it, and everything after it is
rollback noise:

```powershell
Select-String -Path "$env:TEMP\printo-install.log" -Pattern 'Return value 3' |
    Select-Object -First 5
```

Two things are worth checking before reading any further into a log.

A **leftover service** blocks every reinstall. If an earlier attempt failed while something held
the service open - Services, Event Viewer, a Computer Management window - it is left marked for
deletion, `InstallServices` cannot recreate it, and every subsequent install fails the same way
until the machine is restarted:

```powershell
Get-Service PrintoAgent -ErrorAction SilentlyContinue
```

Anything at all here after a failed install means: close those windows, restart, install again.

**Endpoint protection** is the other one. The agent is an unsigned executable that opens a
loopback listener, renders PDFs and talks to printer drivers; some products stop the install
itself rather than the running service. Section 2.7 lists what to exclude.

The Application event log carries the summary as MsiInstaller event 1033, with the product
version and the exit code. **1603** means an action failed and everything was rolled back;
nothing is left installed.

Three failures are worth naming, because between them they cost a day and they are the reason
this section exists.

**The service died on startup, and the install failed with it.** The two executables are
published into one directory, and until 0.1.7 they targeted different Windows TFMs - so the
tray's copy of `Microsoft.Windows.SDK.NET.dll`, the WinRT projection, overwrote the service's
newer one. The service then could not load the assembly its own `deps.json` named, threw a
`FileNotFoundException` out of the OCR probe, and stopped. Because the package waited for the
service to start, that came back as 1603 with everything rolled back. The MSI was well formed
and every file was present; the product simply did not work. Both projects now target the same
Windows TFM, the build refuses to package a publish whose assemblies are older than the apps
ask for, and the OCR probe can no longer take the agent down whatever happens to it.

**A standard user cannot install it.** This package installs a service, so it is per-machine and
needs elevation. From 0.1.6 it says so in a dialog rather than failing with 1603; before that, a
double-click by an operator looked exactly like every other failure. Install as an
administrator, or deploy by Group Policy, which installs as the machine.

**The ACL named accounts in English.** Up to 0.1.1 the package set the data directory's ACL for
accounts named `SYSTEM` and `Administrators`. Windows localises those: on a Polish installation the group is
`Administratorzy`, `Administrators` resolves to nothing at all, the deferred action that applies
the ACL fails, and the install rolls back with 1603. The ACL is now applied by the agent instead, from
well-known SIDs, and a test refuses any account name in the package at all.

Until 0.1.2 the package also had no user interface, so all of that happened behind a progress
window that appeared and vanished - a fatal rollback and a clean install looked identical from
the outside. That is fixed too, and is why the first symptom reported was "it flashes and
disappears".

`Diagnose-Install.ps1`, beside the package, checks the things above on a machine that will not
take the install - elevation, policy, a blocked download, a service left behind - and with
`-Install` runs the installation with a verbose log and names the action that failed.

**Windows Installer itself was broken.** The one that cost the most, and the reason the EXE
exists. A workstation spent a day being blamed for rejecting this package; it was rejecting every
package, including a signed MSI from another vendor and a file name that did not exist — three
lines of log and 1603, before msiexec read a single property. `Diagnose-Install.ps1` now asks
that question first, with a package that cannot exist so that nothing is installed and no
package is implicated: a working machine answers 1619 in about thirty lines, a broken one 1603
in three.

On such a machine, do not spend the day on msiexec. Install `PrintoAgent-<version>.exe` (§2.4a),
which does not use Windows Installer at all, and repair the workstation separately.

### 2.8 What the agent needs on a workstation

- 64-bit Windows 10 22H2 or Windows 11. Both packages refuse to install on 32-bit, and say so.
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
  registration, upgrade sequencing, ACLs, registry values, the payload, no debug symbols — but
  installing it needs elevation. `Verify-Install.ps1` is written for exactly that and has not
  been run. It now also covers the virtual printer end to end: it waits for the queue to appear,
  checks the port points at the agent's endpoint, prints the Windows test page to it, and waits
  for the agent's event-log entry saying the document arrived.
- **The virtual printer has not been bound by the Windows class driver on a second machine.**
  The endpoint, the queue management, the capture and the spooling are built and tested
  (plan §5.0d), and the protocol half is replayed against the session Windows really produced
  during the M1 spike. What no test here can show is Windows binding a queue to this endpoint on
  a machine other than the one that spike ran on — that is what the elevated `Verify-Install.ps1`
  run above is for. Hot folders remain an intake path in their own right, not a fallback.
