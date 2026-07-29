# Online Behavior and Privacy

Demo selection, parsing, conversion, validation, and playback are local. The
application does not upload demos, manifests, replay files, voice sidecars,
local paths, or logs.

| Trigger | Request | Data sent |
| --- | --- | --- |
| App launch, at most once every 24 hours | `releases.detr.site/channels/stable/latest.json` | Installed app version, platform, and normal request metadata |
| The user clicks **Check component update** | `releases.detr.site/channels/stable/playback.json` | Installed playback version, platform, and normal request metadata |
| A desktop update is explicitly confirmed, or the user clicks playback install | `releases.detr.site/releases/...` | Requested release version and normal request metadata |
| A roster is visible | `steamcommunity.com/profiles/<steamid>?xml=1` | SteamID64 and normal request metadata |
| **About & credits** is opened | `avatars.githubusercontent.com` | Public GitHub avatar identifier and normal request metadata |
| A cosmetic image is opened | `cdn.cstrike.app` | Catalog image key and normal request metadata |
| The optional 3D preview is opened | `3d.cstrike.app` | Cosmetic render parameters and normal request metadata |
| The user confirms **Add selected batch** | `inventory.cstrike.app/api/action/resync` and `/api/action/sync` in a collapsible WebView2 side panel | The selected cosmetics' catalog IDs and supported demo-backed wear, seed, name, sticker, and keychain fields |
| An external link is opened | System browser | Normal browser request metadata under the destination's policy |

Steam profile enhancement is automatic, best-effort, and cached for 24 hours.
Failure is silent and never blocks parsing, conversion, or validation. Cosmetic
requests occur only for separately enabled/exported evidence and user-opened
previews.

GitHub avatars are requested only while the user-visible credits board is open.
They identify the already public GitHub accounts named on that page. An offline
or failed request falls back to a local initial and does not affect the app.

The Inventory Simulator integration is user-initiated. Clicking **Add selected
batch** is the confirmation that starts the operation. DemoTracer opens a
resizable side panel on the official site and shows a compact progress indicator.
If needed, Steam sign-in happens only inside that window and the batch resumes
afterward. The window keeps its own normal WebView2 site data so the official
session can be reused, but DemoTracer does not read, export, or store the
session cookie, user ID, or an API key.

Immediately before submission, the official same-origin `resync` route supplies
the current inventory version and inventory document. The document remains
inside the official-origin WebView and is used only to reject duplicates. The
comparison includes item ID, seed, wear, StatTrak state, stickers, keychains,
and patches while intentionally ignoring custom names and inventory-only state
such as equip flags and timestamps. It also detects duplicates within the
selected batch and inside storage units.

New `add` actions are submitted in one request and processed in order by the
official sync route. A concurrent version conflict triggers one fresh resync,
another duplicate check, and one retry. On success the window loads the
refreshed official inventory; its compact completion indicator disappears
automatically. This avoids racing several stale browser tabs.

Each added entry is a simulated replica, not the original owned item:
owner/account identifiers, the original item ID, and the exact StatTrak counter
are not copied. Inventory Simulator does not represent DemoTracer's separate
sticker-scale evidence in this item shape, so that field is not included.
When the demo SteamID resolves to a professional player, supported weapon and
knife replicas use that player's English handle as their custom name. Other
item types are left unchanged because Inventory Simulator accepts custom names
only on name-tag-compatible item types.

Release files are served from the project's Cloudflare R2 custom domain. Update
checks and downloads never include a demo name, demo content, replay content,
Steam install path, local filesystem path, player identity, or generated
manifest. Only the desktop GUI checks for updates automatically. It presents the
current version, latest version, and release notes in a confirmation dialog and
does not download or install until the user approves. Playback update checks and
installs are always manual. Desktop and playback packages are verified with the
public update key embedded in the app before installation.

DemoTracer 1.0 has no telemetry, cloud conversion, replay upload, account
system, or remote player catalog. A future anonymous, consent-based telemetry
design is documented in [TELEMETRY.md](TELEMETRY.md); that design is not active.
