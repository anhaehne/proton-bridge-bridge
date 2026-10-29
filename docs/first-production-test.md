# ProtonBridgeBridge 0.1.0-rc.1: first controlled test

This candidate is ready for an operator-supervised test, with writes disabled by
default. It has not been tested against official Bridge or a running Paperless
consumer. Keep normal mail polling disabled until the checks below pass. No live
account, credentials, test host or authorization to change mailbox data has been
supplied to the development agent; preparing this bundle does not perform a test.

## Files and prerequisites

The portable archive requires the ASP.NET Core 10 runtime. The separate image
archive runs on a Linux Docker host; its Compose file uses host networking to reach
an independently installed official Bridge on localhost. Bridge login, 2FA, vault
and updates remain entirely in Bridge. No Bridge image or vault is bundled.

Verify `ProtonBridgeBridge-0.1.0-rc.1.sha256` with `sha256sum -c` before extraction.
The build JSON records the exact source revision and **local** Docker image ID;
it is not a registry digest. Nothing is automatically published to a registry.

```sh
tar -xzf ProtonBridgeBridge-0.1.0-rc.1-portable.tar.gz
cd ProtonBridgeBridge
mkdir -p local/certs local/state
cp config.example.json local/config.json
# Docker users also load the adjacent image archive:
docker load -i ../ProtonBridgeBridge-0.1.0-rc.1-image.tar.gz
```

Supply your own proxy certificate/key matching the hostname Paperless will use,
and only Bridge's exported public certificate as its trust file. Keep exported
Bridge private keys, vaults and account secrets out of this directory/container.
The sample paths `/certs/proxy.pem`, `/certs/proxy.key`, `/certs/bridge.pem` and
`/state` fit Compose mounts; replace them with host paths for native execution.
Container UID/GID 1654 needs read access to certs/config and write access to state.
Keep the private key and state private. Select unused IMAP/health ports if needed.

## Test sequence

1. **Prepare scope — 10 minutes.** Choose the test host and account, then explicitly
   approve importing the supplied disposable fixture and adding the test label to
   those messages. Prefer a disposable account. If using an existing account, create
   an unused label, a separate Paperless test instance/rule and a state-directory
   backup. Do not point the ordinary production import rule at this candidate.
   `controlled-test.eml` contains one attached PDF and one inline PDF, with a fixed
   Message-ID. Replace `rc1fixture` in its Subject with a unique nonce, import it
   into normal Inbox through your mail client, and import an identical second copy.
   Confirm they have different Inbox UIDs. Set one read/starred and leave the other
   unread/unstarred. Do not perform cleanup or alter other emails.
2. **Discover and check without writes — 5 minutes.** Leave `AllowLabelWrites=false`.
   Provide Bridge-generated credentials through the process environment, never
   command arguments, repository files, issue text or logs. Set the subject filter
   to `ProtonBridgeBridge test <your-unique-nonce>`. Native commands are shown below;
   Compose users can replace `dotnet ProtonBridgeBridge.dll` with
   `docker compose -f deploy/compose.yaml run --rm -e PROTON_BRIDGE_USERNAME -e PROTON_BRIDGE_PASSWORD -e PROTON_BRIDGE_TEST_SUBJECT imap-proxy`.
   Discovery returns only matching UIDs and Inbox UIDVALIDITY; review them and copy
   them into `PreflightUids`, `AllowedWriteUids` and `AllowedWriteUidValidity` in
   `local/config.json`. Preflight checks verified TLS, authentication, UIDPLUS, the
   existing label, repeated raw bytes and read/star preservation, without writing
   labels or state. For two byte-identical imports, verify `CheckedMessages=2` and `DistinctRawMessages=1`; a client that rewrites headers may produce two different raw messages. A successful report still says `LabelCopyTested=false`.
3. **Approve the first label additions — 10 minutes.** After reviewing the scope and
   preflight, set `AllowLabelWrites=true` with the reviewed UID allowlist and its
   UIDVALIDITY, then start the proxy. Use exactly one Paperless polling schedule
   and the isolated test rule: Inbox, tag action with the configured label, no age
   or read-state restriction, attachments including inline parts, blank filename
   filters, and a Subject filter matching only the unique test nonce. Verify the
   proxy hostname certificate in Paperless. Await consumption and the separate tag
   task. Both source emails must remain in Inbox with original read/star states.
   Confirm the label in Proton and Bridge. Identical PDF content may be intentionally
   deduplicated by Paperless; inspect each source UID's processed action separately.
4. **Repeat and restart — 10 minutes.** Poll after all tasks finish, restart the proxy
   while retaining its state directory, and poll again. Confirm no extra COPY or
   repeated import, and that the two independent source UIDs retain exact mappings.
   Run read-only preflight again: label bytes should now match source bytes, while
   the report remains explicit that preflight itself never performs COPY. Review
   hashes and COPYUID results privately if investigating a mismatch; never enable
   raw protocol logging. Reject byte instability, ambiguous matching, missing
   COPYUID, unexpected flags, or UIDVALIDITY changes rather than overriding errors.
5. **Stop and record evidence — 5 minutes.** Disable polling and wait for active
   post-consume actions, stop the candidate, and back up its state. Record source
   revision/image ID, Bridge/Paperless versions and pass/fail results without
   credentials or message contents. Keep this an experimental test until the full
   official-Bridge validation gate has passed. Do not switch the ordinary Inbox rule
   to unattended imports or remove UID scoping on the strength of synthetic tests.

Native commands (edit paths in configuration first):

```sh
dotnet ProtonBridgeBridge.dll --version
dotnet ProtonBridgeBridge.dll --check-config local/config.json
read -r -p 'Bridge username: ' PROTON_BRIDGE_USERNAME
read -r -s -p 'Bridge-generated password: ' PROTON_BRIDGE_PASSWORD
export PROTON_BRIDGE_USERNAME PROTON_BRIDGE_PASSWORD
export PROTON_BRIDGE_TEST_SUBJECT='ProtonBridgeBridge test YOUR-UNIQUE-NONCE'
dotnet ProtonBridgeBridge.dll --discover local/config.json
# Review UIDs and UIDVALIDITY, then edit the three scope fields in local/config.json.
dotnet ProtonBridgeBridge.dll --preflight local/config.json
unset PROTON_BRIDGE_USERNAME PROTON_BRIDGE_PASSWORD PROTON_BRIDGE_TEST_SUBJECT
# Only after explicit approval of the reviewed test-message label additions:
dotnet ProtonBridgeBridge.dll --experimental local/config.json
```

For Compose, the bundled deployment file uses the loaded image, not a source build:

```sh
docker compose -f deploy/compose.yaml up --no-build
```

The proxy serves implicit TLS IMAP on port 1993 and local HTTP health on port 8080
by default. `/health/live` is independent of Bridge; `/health/ready` verifies Bridge
TLS without authenticating. Health is not proof of successful document ingestion.
`--healthcheck` returns 0/1 and requires no credentials. There is no downstream
STARTTLS or plaintext mode.

## Failure and recovery rules

`NOPERM` means writes are disabled, EXAMINE was selected, or a UID is outside the
allowed scope. `LIMIT` means the body or combined scan budget was exceeded; the
connection closes, and no partial message is offered as a complete fetch. Review
`MaxMessageBytes`, `MaxScanBytes` and `MaxMailboxMessages`; do not blindly raise them.
A normal Inbox larger than 10,000 messages needs deliberate adjustment of the
mailbox-count limit even if the test uses only a few messages.

`CANNOT` means identity, journal or UIDVALIDITY needs review. A pending COPY without a persisted exact COPYUID mapping remains blocked by default, even when matching bytes exist or the COPY may not have happened. Keep `AllowHashFallback=false`; it exists only for explicit experimental byte-matching tests. Stop
polling/workers and drain stale actions before restoring or editing state. Do not
clear epoch guards to make an old queued UID writable in a new mailbox epoch.
Certificate changes require reviewed trust replacement; never disable verification.

The UID allowlist limits label additions, not mailbox read access. Empty allowlists
with writes explicitly enabled allow the configured label on any Inbox UID; do not
use that setting in this first test. The proxy necessarily sees Bridge credentials
and decrypted messages. Unknown externally applied labels require review with the default strict identity policy. SHA-256 matching for externally applied labels remains
provisional until byte stability and ambiguity checks pass against stock Bridge.
