# Protocol contract and identity

## Upstream evidence (checked 2026-10-29)

The Kubernetes checkout was clean and remained at `cfbc33e` after fetch and fast-forward pull. Its `paperless.yaml` pins Paperless 3.3.4. No live cluster inspection or changes were performed. Its version is repository evidence, not a new runtime observation.

Paperless's [v3.3.4 mail implementation](https://github.com/paperless-ngx/paperless-ngx/blob/v3.3.4/src/paperless_mail/mail.py) and [dependency lock](https://github.com/paperless-ngx/paperless-ngx/blob/v3.3.4/uv.lock) identify imap-tools 1.15.0. Source inspection and the included real-client synthetic test establish this relevant sequence:

```text
<server greeting, CAPABILITY including X-GM-EXT-1 before authentication>
LOGIN <Bridge username> <Bridge password>   # or AUTHENTICATE PLAIN
SELECT INBOX                              # login defaults to selecting INBOX
SELECT INBOX                              # per rule
STATUS INBOX (UIDVALIDITY)
UID SEARCH CHARSET UTF-8 ((NOT (X-GM-LABELS "Paperless-imported") UNKEYWORD Paperless-imported))
UID FETCH 7,8 (BODY.PEEK[] UID FLAGS RFC822.SIZE)
LOGOUT
# Separate apply_mail_action task after the consumption chord succeeds:
LOGIN <Bridge username> <Bridge password>
SELECT INBOX
SELECT INBOX
UID STORE 7 +X-GM-LABELS Paperless-imported
LOGOUT
```

Actual charset and additional SINCE/FROM/TO/SUBJECT/BODY terms depend on the rule. imap-tools may choose different bulk UID lists. Credentials above are placeholders, never captured from a real account. Paperless creates ProcessedMail records and has its own UIDVALIDITY-aware duplicate handling. Its post-consume task receives UIDVALIDITY, but **does not send that validity with STORE**. The proxy therefore persists an account's initial Inbox UIDVALIDITY and rejects subsequent changes until explicit operator recovery. This prevents an old queued action from applying to a reused UID in a new mailbox epoch.

The [original report](https://github.com/paperless-ngx/paperless-ngx/discussions/12133) dates to 21 February 2026. [Gluon #450](https://github.com/ProtonMail/gluon/pull/450) and [Bridge #521](https://github.com/ProtonMail/proton-bridge/pull/521) remain open. Gluon is a synchronization server rather than a drop-in proxy and the proposed extension is opt-in. [proton-mail-mcp's documented label operation](https://github.com/sethbang/proton-mail-mcp#update_message_labels) corroborates additive COPY to `Labels/<name>`; that project's Message-ID-based removal is not used here. These are design evidence, not proof of behavior in the installed official Bridge version.

## Allowed command surface

| Downstream command | Behavior |
| --- | --- |
| CAPABILITY, NOOP, LOGOUT | Local; no destructive upstream cleanup |
| LOGIN, AUTHENTICATE PLAIN | Credentials validated by MailKit against verified-TLS Bridge; no local account store |
| SELECT / EXAMINE INBOX | Exposes original upstream UID namespace; SELECT is writable only for the configured label extension |
| STATUS INBOX | MESSAGES, UIDNEXT, UIDVALIDITY |
| LIST / LSUB | Minimal INBOX-only view for Paperless; not a complete folder listing implementation |
| UID SEARCH | Boolean groups, NOT/OR, UID, normal read/star/answer/delete/draft/recent flags, date and common text/header terms; configured-label X-GM-LABELS/KEYWORD/UNKEYWORD |
| UID FETCH | UID, FLAGS, RFC822.SIZE, BODY.PEEK[], configured-label X-GM-LABELS |
| UID STORE +X-GM-LABELS[.SILENT] | Only the configured label; bare or one-item list; translated to UID COPY |
| CLOSE / UNSELECT | Local selection release; never expunge |

Other operations return BAD or NO without forwarding mutations. Unknown labels/search keys are rejected instead of returning plausible empty results. General sequence-number FETCH, BODY[] that marks seen, partial/header fetches, IDLE, arbitrary flag STORE, APPEND, CREATE/DELETE/RENAME, MOVE, COPY to client-specified targets, EXPUNGE and label removal are unsupported. The capability advertises only the subset Paperless requires; this is not a full Gmail-compatible server. Use a simple ASCII tag such as `Paperless-imported`: Paperless's bare STORE parameter cannot safely express every label containing whitespace or protocol delimiters.

The IMAP reader parses nested lists, atoms, escaped quoted strings and synchronizing/non-synchronizing octet-counted literals. Commands have bounded size/depth; malformed framing closes the connection. It does not use line splitting or regex to rewrite IMAP. MailKit's mature parser and serializers handle all upstream responses and requests. The frontend parser is project-owned and intentionally small; it needs broader interoperability/fuzz testing before general use.

## Identity and recovery

A UID belongs to a mailbox's UIDVALIDITY epoch, not to an entire account. No assumption is made about Message-ID. Raw complete BODY.PEEK[] octets are hashed without MIME reserialization or header normalization.

For an addition, the proxy serializes membership reconciliation and COPY across all sessions, persists a pending source UID/validity/hash intent, and sends COPY to the pre-existing configured label. UIDPLUS/COPYUID is required **before attempting COPY**. After COPY it reads the returned label UID, verifies byte equality and label UIDVALIDITY, then atomically replaces the journal with the completed mapping. A reconnect reloads the journal, so simultaneous sessions cannot overwrite stale in-memory state. One exclusive state-directory handle prevents multiple processes using the same directory on filesystems supporting normal file sharing locks.

Known COPYUID mappings identify identical deliveries independently. Unexplained label membership can use hashes only when exactly one Inbox message is a candidate. Additional unexplained copies of identical content cause NO, even if one identical delivery already has a known mapping. Existing externally labeled identical deliveries, lost COPYUID responses and ambiguous crash recovery require operator inspection; the proxy never guesses. Known mappings whose octets change also fail. Unique pending copies can reconcile by hash; pending intents without evidence block further additions rather than blindly retrying. An externally removed label makes a known source eligible again.

Files are flushed before rename, supporting process-restart recovery. Filesystem/power-loss guarantees and network/PVC file-lock semantics require deployment-specific validation; this is not a transactional distributed store. A journal failure after a successful remote COPY leaves an uncertain intent. Never discard state while workers can still apply queued actions.

Inbox UIDVALIDITY changes block all subsequent selections, including new connections. For recovery, stop Paperless mail polling and post-consume workers, drain or cancel stale queued actions, stop the proxy, and back up state. Review changed UID mappings and Paperless's ProcessedMail handling before resetting the affected account's `.json.epoch` file and restarting with a disposable verification run. A new epoch can make content eligible again; ambiguous identical messages remain blocked. Do not automate epoch reset or edit state while the service runs.

Mailbox membership changes within a downstream selection fail UID commands rather than emitting incorrect sequence numbers. Reconnect/reselect to refresh. Per-command serialization prevents overlapping local COPY operations; it does not reserve messages across the time Paperless asynchronously consumes documents. Concurrent Paperless pollers can queue the same unprocessed message before its label is applied. Configure one polling schedule and let actions complete. Exactly-once document ingestion across remote COPY, task crashes and Paperless's database is not claimed.

Hash byte stability across folders and Bridge rebuilds remains an open live validation item. If Bridge emits different raw bytes for the same underlying message, unknown external label matching may miss membership. This is a release blocker, not something a SHA-256 unit test can prove. No production-safe identity claim is made until the official-Bridge gate passes.
