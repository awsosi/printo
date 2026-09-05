# TLS certificates

Traefik reads its default certificate from this directory:

    printo.crt    certificate, with any intermediates appended
    printo.key    private key, unencrypted

Neither file is in the repository, and `.gitignore` keeps them out. Place them here, then
enable the store:

    cp ../dynamic/tls.yml.example ../dynamic/tls.yml

Then restart the proxy:

    docker compose -f infra/docker-compose.prod.yml restart traefik

Traefik does watch the dynamic directory and reloads routing changes in place, but the watch is
driven by inotify, which does not fire for a file created on a bind mount from a Windows or
macOS host. Restarting takes two seconds and works everywhere; routing changes to
`printo.yml` on a Linux host are picked up without one.

**With no certificate present the stack still serves HTTPS**, using a self-signed certificate
Traefik generates for itself. Browsers warn, and the Windows agents refuse to connect until the
CA is trusted — which for a domain-joined fleet means issuing this certificate from the same
internal ADCS that signs the agent MSI, so the chain is already trusted everywhere by GPO.

Do not copy `tls.yml.example` into place before the two files exist. A default certificate that
names files which are not there does not degrade gracefully: Traefik fails to build the
certificate store, and then *every* TLS handshake fails, so the stack looks healthy and serves
nothing. That is exactly why the store is a separate optional file rather than part of
`printo.yml`.

To issue one from ADCS (run on a domain-joined machine, as an operator who may enrol
certificates):

    certreq -submit -attrib "CertificateTemplate:WebServer" printo.req printo.crt

Convert the resulting key and certificate to the two file names above. If the CA hands back a
PFX instead:

    openssl pkcs12 -in printo.pfx -clcerts -nokeys  -out printo.crt
    openssl pkcs12 -in printo.pfx -nocerts -nodes   -out printo.key

Restart Traefik after replacing a certificate as well - the file contents change, not the
configuration, so nothing signals the reload on its own.
