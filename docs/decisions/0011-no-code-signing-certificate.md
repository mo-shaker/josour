# ADR-0011: no code-signing certificate — one file, verified by its hash

**Status:** **accepted by the product owner on 2026-09-10.** Amends [ADR-0005](0005-installer-unpackaged.md) by
dropping the signing requirement, and amends success criterion 1 in the product document.

## Context

A code-signing certificate has been on the list since week one: "request it on day one, issuance takes days." It
stayed open for seven weeks, and success criterion 1 stayed blocked on it alone.

Then a number in the product's definition changed: **three to five users at most**, and the product owner knows
each of them personally.

## Decision

**No certificate is bought.** `Josour.exe` is distributed as a single unsigned file, and each user gets past the
SmartScreen warning **once on their machine** after checking the file's hash.

## Reasons

**What a certificate actually buys is trust at scale** — a thousand strangers trusting software whose source they
have not seen. With five people who know who wrote the program and who handed them the file, that trust already
exists by a shorter and more reliable route.

**And the certificate does not give what it is assumed to give.** An OV certificate **does not remove
SmartScreen** — it builds reputation gradually through download counts, and five downloads build no reputation. An
EV certificate has not granted instant reputation since August 2024. So paying might not remove the warning at all
at this scale.

**The alternative here is stronger than signing, not weaker.** A signature proves the file came from someone who
bought a certificate. **A SHA-256 hash read out over a phone call proves the file is this exact file** — which,
for five people, is both easy and conclusive.

## Consequences

- **Success criterion 1 cannot pass as written**, and has been restated in
  [`acceptance-checklist.md`](../acceptance-checklist.md): a SmartScreen warning appears and is bypassed once per
  machine, and what is required is that the hash matches.
- **`scripts/publish-exe.ps1` is the source of the hash**: it prints one on every build, and refuses to call the
  output shareable if any file is left beside the exe.
- **The Inno Setup installer in `client/installer/` has left the critical path.** The firewall rule it used to add
  stopped being a requirement after [ADR-0009](0009-relay-default.md): with the relay, neither side opens a
  listener.
- **Milestone M4 is no longer a release gate.** What remains in it is deferred, not deleted.
- **This decision is revisited if usage grows beyond a known circle.** The threshold is not a number in a licence
  but a question: does every user know who gave them the file? When the answer becomes "no", the certificate is
  back on the table — and with it auto-update, which unsigned is a distribution channel with nothing to prove.

## What does not change

The file **is not published publicly** and does not go on an open download page. The decision is to do without
signing **inside a known circle**, not to do without verification.
