"""Paperless 3.3.4's imap-tools 1.15.0 path against synthetic C# TLS fixtures.
No Proton account, real emails or Paperless database is involved.
"""
import ssl
import sys
from imap_tools import AND, NOT, MailBox

port, ca = int(sys.argv[1]), sys.argv[2]
context = ssl.create_default_context(cafile=ca)
criteria = AND(NOT(gmail_label="Paperless-imported"), no_keyword="Paperless-imported")

def mailbox():
    return MailBox("localhost", port=port, ssl_context=context).login("fixture", "secret")

with mailbox() as box:
    assert "X-GM-EXT-1" in box.client.capabilities
    box.folder.set("INBOX")
    assert box.folder.status("INBOX", ["UIDVALIDITY"])["UIDVALIDITY"] == 10
    uids = box.uids(criteria=criteria, charset="UTF-8")
    assert uids == ["7", "8"], uids
    messages = list(box.fetch(uid_list=uids, mark_seen=False, bulk=500))
    assert len(messages) == 2
    assert all("\\Seen" in m.flags and "\\Flagged" in m.flags for m in messages)
    assert all(m.attachments[0].content_type == "application/pdf" for m in messages)
    assert all(m.attachments[0].content_disposition == "inline" for m in messages)

# Paperless's apply_mail_action opens a new connection after consumption completes.
for uid in ["7", "8"]:
    with mailbox() as box:
        box.folder.set("INBOX")
        status, _ = box.client.uid("STORE", uid, "+X-GM-LABELS", "Paperless-imported")
        assert status == "OK"
    with mailbox() as box:
        box.folder.set("INBOX")
        remaining = box.uids(criteria=criteria, charset="UTF-8")
        assert remaining == (["8"] if uid == "7" else []), remaining

print("Paperless imap-tools sequence passed: inline PDFs, read/star preservation, independent identical deliveries, repeated polls.")
