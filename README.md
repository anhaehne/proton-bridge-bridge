# Proton Bridge compatibility proxy

Experimental C#/.NET 10 IMAP adapter for Paperless-ngx and **official, unmodified Proton Mail Bridge**. MailKit handles upstream IMAP, authentication, MIME octets and TLS; a bounded recursive-descent IMAP parser handles the deliberately small downstream command surface. No Proton login, 2FA, decryption or update code is included.

**State:** implemented and tested with synthetic TLS IMAP fixtures and Paperless 3.3.4's exact `imap-tools` 1.15.0 client dependency. Not tested against official Bridge or a running Paperless consumer. No real account, deployment or production release has been used. The executable requires `--experimental`; use disposable accounts until the [live validation gate](docs/validation.md) passes.

## What it does

Paperless connects using implicit TLS and its Bridge-generated username/password. The proxy validates those credentials through Bridge using mandatory STARTTLS or implicit TLS. It exposes normal `INBOX` with original UIDs and UIDVALIDITY, fetches with `BODY.PEEK[]`, and translates the configured processed label into `UID COPY` to an existing `Labels/Paperless-imported` mailbox. It never creates a label automatically.

`X-GM-LABELS`, `KEYWORD` and `UNKEYWORD` for the configured label share one membership calculation. COPYUID mappings distinguish byte-identical deliveries; SHA-256 of complete raw IMAP octets provides a **provisional, unvalidated-on-Bridge** fallback for labels created outside this proxy. Ambiguous matches stop the operation. Message-ID is never used for identity.

This implements the Gmail-label subset needed by Paperless, not the complete Gmail extension. It advertises `X-GM-EXT-1` before authentication because Paperless uses that capability to choose its tag action. Other Gmail features, arbitrary labels, message deletion, moves, flag changes and expunge are rejected. Use only with the documented Paperless rule, not as a general mail server. See [protocol and identity](docs/protocol.md).

## Docker Compose (primary example)

The [Compose example](deploy/compose.yaml) assumes a **Linux host already running official Bridge**. Host networking allows the proxy to reach Bridge's localhost listener. It installs no unofficial Bridge image and mounts no Bridge vault or account secrets. Docker Desktop and separate-host installations need an explicitly designed shared network namespace or local tunnel; plain container DNS does not reach Bridge's loopback listener.

1. Install and sign into official Bridge independently. Create a disposable test label `Paperless-imported` in Proton. Export Bridge's public TLS certificate through its settings; keep the exported private key away from the proxy. Check the certificate's SANs against `BridgeHost` (normally `localhost`).
2. Create `local/certs` and `local/state`. Copy [config.example.json](deploy/config.example.json) to `local/config.json`. Supply a proxy certificate/key matching the hostname Paperless will use as `local/certs/proxy.pem` and `local/certs/proxy.key`. Save only Bridge's public certificate as `local/certs/bridge.pem`. Give container UID/GID 1654 read access to these files and write access to `local/state`; keep the private key and state private.
3. Build and start: `docker compose -f deploy/compose.yaml up --build`. The default listener is `127.0.0.1:1993`. For remote Paperless, set `ListenAddress` to an appropriate host address and restrict the firewall to Paperless. This is an experimental foreground service with no automatic restart.
4. Configure Paperless as below and run the [disposable validation procedure](docs/validation.md). Keep the state directory on persistent local storage and back it up together with the configuration. Run one proxy process against a state directory; multiple replicas are unsupported.

For a local disposable proxy certificate, for example (replace `proxy.example.test` with the actual hostname):

```sh
openssl req -x509 -newkey rsa:3072 -sha256 -days 7 -nodes \
  -keyout local/certs/proxy.key -out local/certs/proxy.pem \
  -subj /CN=proxy.example.test -addext subjectAltName=DNS:proxy.example.test
```

Trust that public certificate in Paperless's CA store. Keep account certificate verification enabled. A locally generated proxy certificate is separate from Bridge's exported trust certificate.

## Paperless settings

Use IMAP **SSL/implicit TLS** to the proxy hostname on port 1993, with Bridge-generated credentials. The proxy does not offer downstream STARTTLS. Trust the proxy certificate/issuing CA in the Paperless process, for example via a custom combined CA bundle mounted read-only and `SSL_CERT_FILE=/path/to/bundle.pem`. Restart Paperless workers after changing process environment or trust.

Create a mail rule with folder `INBOX`, action **Tag the mail with specified tag, don't process tagged mails**, parameter `Paperless-imported`, attachment processing including **inline attachments**, and blank attachment filename inclusion/exclusion filters so unnamed or unusually named PDFs are not excluded. Paperless can also consume other supported attachment formats with this rule. Disable the age limit (`maximum_age=0`) and remove sender/body filters if all historical PDFs are intended. The tag action supplies no UNSEEN constraint, so already-read messages remain eligible. Do not choose star, mark-read, move or delete actions.

Paperless controls MIME recognition and document consumption; the proxy supplies full message octets. If you choose filename filters such as `*.pdf`, they can miss unnamed or misleadingly named PDFs. Verify these cases in the validation gate. Paperless's parser, not this proxy, decides whether a part is a supported document.

## Native and Kubernetes

Native: install the pinned SDK, then run:

```sh
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet restore --locked-mode
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish src/ProtonBridgeProxy -c Release --no-restore -o artifacts/proxy
# Adjust all file paths and permissions in local/config.json for this host.
dotnet artifacts/proxy/ProtonBridgeProxy.dll --experimental local/config.json
```

Run as an unprivileged user alongside Bridge. Supervise it with your normal service manager after the validation gate. Stop it with SIGINT; service termination drops connections without expunging messages.

Kubernetes: [kubernetes.example.yaml](deploy/kubernetes.example.yaml) is a **template requiring edits**, not a deployment destination. Merge the proxy container into the Pod already running official Bridge so both share localhost. Preserve Bridge's own installation and secret mounts; the proxy gets only its certificate/key, Bridge's public trust certificate, config and a persistent state volume. Set `ListenAddress=0.0.0.0`, issue the proxy certificate for its Service DNS, and configure Paperless to verify that name. Use one replica with Recreate, an image digest from the reviewed build, and a NetworkPolicy admitting TCP/1993 only from Paperless. Apply your cluster's existing namespace/PVC/Secret conventions. There is no supplied Bridge image or live manifest change.

## Configuration and trust

See [config.example.json](deploy/config.example.json) for every field. `BridgeTls` accepts only `StartTls` or `SslOnConnect`; neither permits plaintext fallback. `BridgeTrust` is an optional path to **one PEM trust anchor**, typically Bridge's exported self-signed certificate. When absent, system trust applies. A custom anchor retains hostname, certificate validity and chain verification. Local Bridge certificate revocation is disabled because its generated certificates have no revocation service. Custom-chain downloads are disabled. Certificate/key paths are local files, never certificate material in JSON.

Bridge stores its default certificate/key in its vault; current upstream also supports custom PEM paths. It can fall back to its vault certificate if those files fail to load. After replacement, re-export and review its public certificate and update trust; do not disable verification. New upstream sessions reload the trust file; restart the proxy to reload its listener certificate/key. Sharing Bridge's certificate as the listener certificate is optional, requires valid hostname SANs and private-key handling, and is not the example's design. [Upstream certificate code](https://github.com/ProtonMail/proton-bridge/blob/master/internal/vault/certs.go).

The proxy sees Bridge credentials and decrypted emails. Its allowed commands restrict normal client operations, but its underlying credentials can access the account. PDF/Inbox filters are not a mailbox access boundary. No credential/content/protocol logging, telemetry or external service is implemented. State files contain account-scoped hashes, UIDs, UIDVALIDITY and message fingerprints; protect them as sensitive metadata. Runtime network use is client IMAP and configured Bridge IMAP. Build tools fetch packages/images separately.

Defaults cap connections at 8, command frames at 64 KiB, literals at 16 KiB, nesting at 24, message reads at 32 MiB and each scanned mailbox at 10,000 messages. The 300-second timeout covers the entire connection. Membership searches currently rescan full INBOX and label messages sequentially; this is an intentionally conservative prototype with substantial cost on large mailboxes. Raise configured limits only after measuring disposable workloads. MailKit may buffer fetched data before the application's message-size check.

## Verification and releases

```sh
python3 -m venv /tmp/proxy-wire-tests
/tmp/proxy-wire-tests/bin/pip install --require-hashes -r tests/requirements.txt
PAPERLESS_TEST_PYTHON=/tmp/proxy-wire-tests/bin/python dotnet test -c Release
```

Without `PAPERLESS_TEST_PYTHON`, the Python client test is explicitly skipped; the C# parser, identity and verified-TLS integration suite still runs. The Python test exercises the actual pinned imap-tools commands with synthetic inline PDF messages, including read/star flags and identical deliveries. It does **not** start Paperless, OCR, its task queue, or official Bridge.

The [build workflow](.github/workflows/build.yml) restores locked NuGet dependencies, runs all tests, publishes a review artifact and builds the container. It has no registry credentials, automatic publish or deployment step. Follow the [release checklist](docs/validation.md#review-and-release) before calling a build compatible with official Bridge. Dependency versions and container base digests are pinned; review updates deliberately.

## C# style checks

Follow [AGENTS.md](AGENTS.md) and `.editorconfig`. Apply formatting with
`dotnet format --no-restore`. Before committing C# changes, run:

```sh
dotnet format --no-restore --verify-no-changes
dotnet format style --no-restore --verify-no-changes --diagnostics IDE0011
```

The second command verifies braces on control-flow bodies. Review logical grouping,
comment placement and async cancellation manually, then run the full verification
suite above. Style refactors must preserve behavior.
