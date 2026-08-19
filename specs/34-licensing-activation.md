# M-Licensing — activation, seat management and the startup check

> **Why this milestone exists.** CodeScenes is a paid tool and nothing currently distinguishes a
> customer from anyone who copied the package folder. This milestone adds license activation, a
> 14-day trial, and a periodic entitlement check, entirely inside the editor.
>
> **It is subordinate to the product invariant, not an exception to it.** The contract says sync
> fires on every change, must be effectively instant, and the happy path has no buttons. A licensing
> check that blocks domain reload, stalls startup, or asks the user to press something on a normal
> launch violates that. A licensed user in steady state must never see this feature at all: no
> dialog, no banner, no network wait, no added reload cost.
>
> The server half — the activation API, Gumroad verification, the seat store and token signing —
> is `CodeScenesSite/specs/01-licensing-backend.md`. That spec owns the wire contract; this one
> consumes it. The editor never calls Gumroad and never touches Firestore.

---

## Concrete backend values (the wire this consumes)

The backend is deployed. Every call is a `POST` of JSON `{ action, ... }` to the one endpoint; the
action is one of `activate` / `trial` / `seats` / `release` (`CodeScenesSite/specs/01` §3).

```
Endpoint  https://us-central1-codescenes.cloudfunctions.net/license   (override via a single constant for local/emulator testing)
PublicKey CodeScenesSite/functions/keys/license-public.jwk.json        (JWK n/e, RSA-2048; embed as a literal, see §7)
Rejection reasons: invalid_key | refunded | seat_limit | rate_limited | trial_expired | invalid_request
```

The transport is behind an injectable seam so EditMode tests mock it (spec Coverage); the endpoint
constant is overridable so a live check can point at the deployed function or a local emulator.

---

## Why activation is in the editor and not on the website

The purchase is a web flow: someone reads about CodeScenes anywhere, buys on Gumroad, and the key
arrives by email. That path needs no editor and stays on the site.

Activation is the opposite. The machine being licensed is the machine running Unity, so routing
activation through a browser means passing a machine identifier through a URL, and it strands any
user who buys and then loses the tab, because the identifier cannot be reconstructed by hand. Key
entry in the editor collapses the flow to paste-and-click and keeps the machine identifier on the
machine it identifies. The tools people cite as premium (JetBrains, Sublime, Unity's own Hub)
activate in-app for the same reason.

---

## Two distribution channels, two builds

CodeScenes ships through the Unity Asset Store and through Gumroad, as the same complete product on
both. Nothing enhanced is sold off-store, which is what keeps the arrangement clear of Provider
Agreement §4.9.1.2 (see `CodeScenesSite/research/04-asset-store-distribution.md`).

An Asset Store purchase already carries Unity's own per-seat EULA. That copy needs **no key, no
activation, no seat management, no trial, and no contact with the backend at all**. Everything in
this spec applies to the Gumroad build only.

The 14-day trial is therefore a **Gumroad-channel feature**, not a product-wide one. An Asset Store
customer has bought the tool outright and has nothing to trial. This asymmetry is a selling point
for the direct channel and the site should say so.

The channel is a **build-time** decision, not a runtime check: a define constant set when the package
is packed, not something inspected on the user's machine. Shipping the activation UI to Asset Store
customers is the failure that matters here, because it presents a License window demanding a key they
were never issued and have no way to obtain.

It must **fail closed**. A build with no channel define set behaves as the Gumroad build and requires
activation. A packaging mistake that silently disables licensing is worse than one that surfaces
immediately, and only one of those is noticed before release.

**Build:** the channel define, an Asset Store build with the licensing assemblies excluded rather
than merely disabled, and the fail-closed default.

**Accept when:** the Asset Store build contains no licensing UI, makes no network call to the
backend, and exposes no `Code Scenes > License` menu item; a build with the define absent requires
activation.

---

## 1. License state and where it lives

One persisted token, one derived state. State is `Licensed`, `Trial`, or `Unlicensed`.

The signed token from the backend is cached in **`EditorPrefs`**, keyed per machine and per user.
It must **never** be written anywhere under `Assets/`, and never inside the project directory: a
file under `Assets/` triggers a domain reload on write, which is exactly the cost the contract
forbids, and it would land in the user's version control and be shared with their team.

Resolved state must survive domain reloads without re-doing work. Cache it in `SessionState` so a
reload reads a cached verdict rather than re-validating, and so nothing re-hits the network within
a session.

**Build:** token persistence in `EditorPrefs`, resolved state cached in `SessionState`, and a single
`LicenseState` accessor the rest of the adapter reads.

**Accept when:** activating, then triggering ten domain reloads, produces exactly one network call,
and no file is created under `Assets/` or inside the project directory at any point.

---

## 2. The startup check must not be on the critical path

`InitializeOnLoad` runs during domain reload. A synchronous `UnityWebRequest` there stalls every
reload by a network round trip, on a tool whose entire premise is that reloads are the thing to
avoid.

The check is therefore fully asynchronous, and throttled to **at most one attempt per calendar day**.
A cached token whose `exp` is still in the future is valid on its own signature, so the editor opens
and syncs immediately regardless of what the check is doing, or whether it runs at all.

Behavior while a check is in flight is **fail-open**. Sync runs. A user whose token is valid must
never wait on a network response to edit their scene, and a transient outage must not present as a
licensing failure.

**Build:** an async check scheduled off the reload path, attempted at most once per day, that never
blocks sync while pending.

**Accept when:** with the network fully unreachable and a valid cached token, editor startup and
sync latency are indistinguishable from an unlicensed-code build, and the console is silent.

---

## 3. Expiry and the trial

**One clock, not two.** The offline budget is the token's own lifetime. Every successful refresh
issues a new token expiring 14 days out, so "time since the last successful contact" and "time until
the token expires" are the same number, and there is no separate grace period to track.

| | Value | Meaning |
|---|---|---|
| Offline budget | 14 days | `exp` on a license token; every success resets it to 14 |
| Refresh cadence | at most 1×/day | Attempted while the editor is open; failure is silent |
| Trial | 14 days from first claim | Server-stamped, reissue never extends it |

A user who opens Unity online on any day inside a fortnight never sees this feature. A user who goes
offline has 14 days from their last successful check. That is the plane case.

Failure to refresh is not a licensing failure. An unreachable server is silent and the token keeps
running to its expiry. **A reachable server that rejects is immediate**: `invalid_key` or `refunded`
moves to `Unlicensed` at once, without waiting for the token to expire. Unreachability rides the
clock out; denial does not.

Because the check is daily rather than at expiry, a refund takes effect the next day for an online
user, and only stretches to 14 days for someone who is genuinely offline.

The last few days before expiry show a non-modal banner naming the days remaining, so an offline
user finds out before the tool stops rather than after.

The trial is claimed by calling `trial` on first run with no license, and it **requires network on
first run**. A user who installs entirely offline gets no trial and sees a message saying so rather
than a silent failure. The trial's `exp` is the server's `expiresAt`, so reinstalling reissues a
token for the *remaining* time, not a fresh 14 days.

**Clock tampering:** persist the last observed UTC time. If the system clock is ever behind it by
more than a small skew, force an online check rather than honoring the local clock. Without this,
setting the clock back extends the trial and the offline budget indefinitely.

**Build:** the durations as named constants, the once-per-day refresh throttle, the
reject-immediately rule, the clock-rollback check, and the offline-first-run trial message.

**Accept when:** a token 13 days old with the network down still syncs; the same token at 15 days
does not; a token of any age becomes `Unlicensed` immediately once a reachable server returns
`refunded`; a successful refresh on day 13 restores a full 14 days; and setting the system clock
back 30 days extends nothing.

---

## 4. What unlicensed actually does

**This is the product decision in this milestone, and it is confirmed.** Build exactly as specced
below.

`Unlicensed` stops auto-sync in both directions and disables the Build/Sync menu items. It does
**not** modify, revert or delete anything, and it never leaves a sync half-applied: an in-flight
sync completes before enforcement takes effect. A scene and its builder source that were in sync
stay in sync, frozen, until the tool is activated again.

The rationale is that half-applied state is the one outcome worse than a stopped tool. A user whose
license lapses mid-project must find their work exactly as they left it.

A persistent banner offers Activate. That is the only place in the feature where a button appears
on a path the user did not choose, and it appears only when the tool is not working anyway.

**Accept when:** transitioning to `Unlicensed` during an active sync leaves both scene and source
at a consistent state, and re-activating resumes sync with no manual step and no data loss.

---

## 5. The activation window

`Code Scenes > License`. One `EditorWindow`, three states.

**No license:** a key field, an Activate button, and a "Buy CodeScenes" button that
`Application.OpenURL`s the Gumroad product page. If a trial is running, show days remaining.

**Activating:** the window calls `activate` with the key, the machine identifier, and a
human-readable label and OS string. On `seat_limit` the response already carries the seat list, so
the window switches straight to seat management with a message explaining why, without a second
round trip.

**Licensed:** show seats used out of 3, and the seat list. The current machine is visually marked.
Each row shows label and OS so a user picking one to remove is not choosing between hex strings.
Remove calls `release`.

Messaging at the activation moment is what makes this feel finished. "Activated. 2 of 3 seats in
use." beats a silent success, and "This key is already on 3 machines" with the list right there
beats a generic failure.

The window is UI Toolkit (UXML/USS) rather than raw IMGUI, so it can carry the product's own
styling. This is the one screen a paying customer looks at before the tool disappears into the
background.

**Build:** the window, the three states, and the seat list with labels.

**Accept when:** a user with 3 seats consumed can, from the activation window on a fourth machine
and without leaving the editor, see which machines hold the seats, remove one, and activate.

---

## 6. Machine identity

The identifier is sent to `activate` and hashed server-side; the raw value is never stored remotely.

The identifier is `SystemInfo.deviceUniqueIdentifier`. It is **not reliably stable** in general (OS
reinstall, some hardware changes, and it behaves differently across platforms), and each change
silently consumes one of three seats. §5's seat list is the mitigation and must be reachable from an
unlicensed state.

Measured on Linux (2026-08-18): `SystemInfo.deviceUniqueIdentifier` returns the exact contents of
`/etc/machine-id` — the canonical stable machine id, unchanged across editor restart and reboot,
changing only on OS reinstall. So on Linux the built-in value is the right one. Windows and macOS
legs are still to be measured (a committed probe exists); until then the identifier is
`deviceUniqueIdentifier` on all platforms, and §5 covers the instability cases.

**Build:** send `SystemInfo.deviceUniqueIdentifier` as the machine identifier to `activate`. Do not
re-hash it client-side; the server hashes it.

**Accept when:** activating twice from the same editor across a restart consumes exactly one seat
(the identifier is stable within a machine), and the seat list in §5 lets a user reclaim a seat held
by a machine whose identifier has since changed.

---

## 7. Token verification

The backend returns an **RSA-2048 / PKCS#1 v1.5 / SHA-256** signed token. The public key is embedded
in the package as a literal (JWK `n`/`e`, base64url); verification is offline via
`RSACryptoServiceProvider.ImportParameters` + `VerifyData(bytes, sig, SHA256, Pkcs1)`.

ECDSA is not an option: on the target editor (Unity 6000.5.3f1, Mono profile) `ECDsa.Create()`
implements neither `ImportSubjectPublicKeyInfo` nor `ImportParameters` — both throw
`NotImplementedException` — and the `.NET 5` `DSASignatureFormat` overload of `VerifyData` is absent.
RSA import and verify are implemented on the same profile. Measured on Linux 2026-08-18 (a Node-signed
RSA-2048 vector verifies, a tampered byte fails, both offline); confirm on Windows and macOS.

**Wire format** (matches the backend, `CodeScenesSite/functions/src/licensing/token.ts`): the token
is `b64url(payloadJson) "." b64url(signature)`. The signature covers the **ASCII bytes of the first
segment** (JWS-style signing input), so the editor verifies the exact bytes it received without
re-serializing. Split on `.`, verify the signature over `ascii(segment[0])`, then base64url-decode
`segment[0]` to the JSON payload `{ v, kind, sub, lic, iat, exp }` (iat/exp are unix **seconds**).
The public key is published at `CodeScenesSite/functions/keys/license-public.jwk.json` (JWK `n`/`e`)
and embedded in the package as a literal.

A token is valid only if the signature verifies, `sub` matches this machine's hash, and `exp` is in
the future. All three, every time. A token issued for another machine must fail even though its
signature is genuine.

**Accept when:** a token with any byte altered fails, a token for a different machine fails, and
verification runs with no network access, on all three desktop platforms.

---

## Coverage

Per the contract, Unity-observable behavior needs EditMode coverage in `unity-gate/Assets/GateTests/`
against a live editor, and `./verify.sh` must report `GATE PASS`. The network boundary is mocked at
the transport seam so tests never call the real backend; the token verification, expiry and
clock-rollback logic are all pure and testable without one.

The states worth covering are the ones that are easy to get wrong and invisible when they are:
expiry exactly at the boundary, silence on unreachable versus immediate lock on rejection, a valid token for
the wrong machine, clock rollback, and enforcement arriving mid-sync (§4).
