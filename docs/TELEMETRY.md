# Anonymous Product Telemetry Design

Status: design only. No telemetry endpoint or desktop collection is active.

## Purpose

Telemetry should answer a small set of product questions:

- Which released GUI versions are active?
- Which major workflows are used and whether they succeed?
- How long conversion and batch workflows take in broad terms?
- Whether GUI and playback updates succeed?
- Which safe failure codes deserve engineering attention?

It is not a logging, crash-dump, user-profile, or demo-inspection system.

## Consent

The first implementation should ask once, with two equally clear choices:
**Share anonymous usage statistics** and **Do not share**. The choice must remain
available under Settings and disabling it must stop collection immediately.
There is no telemetry before the user makes that choice.

## Architecture

```text
Desktop allowlisted event
  -> bounded in-memory batch
  -> POST https://telemetry.detr.site/v1/events
  -> Cloudflare Worker schema validation
  -> Workers Analytics Engine dataset
  -> private SQL API queries
```

The desktop Rust layer owns validation, batching, and network delivery. React
may request a named event through a Tauri command, but it cannot send arbitrary
JSON or free-form properties. Telemetry failure is silent and never blocks app
startup, conversion, validation, installation, or shutdown.

The Worker does not write request IP, country, User-Agent, referrer, or request
headers to the dataset and does not enable application request logging. The
public ingestion endpoint has no embedded client secret: secrets shipped in a
desktop executable are not secrets. It instead relies on a small request limit,
strict enums, a maximum body size, a maximum batch size, and anomaly filtering.

## Identity Boundary

Generate one random seed in local application data and never transmit it. When
telemetry is enabled, derive a daily identifier locally from the seed, UTC date,
and a versioned domain separator. Transmit only the truncated derived value.

This permits deduplication within one UTC day and daily-active-install estimates,
but deliberately prevents cross-day user histories and retention analysis. A
stable installation identifier must not be introduced without a separate,
explicit product decision.

## Initial Event Allowlist

| Event | Result values | Optional numeric values |
| --- | --- | --- |
| `app_started` | `success` | none |
| `update_check_completed` | `current`, `available`, `failed` | check duration |
| `update_install_completed` | `success`, `failed` | download bytes, duration |
| `conversion_completed` | `success`, `failed`, `cancelled` | duration, exported file count |
| `batch_completed` | `success`, `partial`, `failed`, `cancelled` | duration, item count |
| `archive_opened` | `success`, `failed` | round count |
| `playback_install_completed` | `success`, `failed`, `rolled_back` | installed file count |
| `environment_check_completed` | `pass`, `warning`, `error` | check count |

Every event may include only these common dimensions:

- telemetry schema version;
- event name;
- released app version and release channel;
- UI language;
- platform family (`windows`) and architecture (`x86_64`);
- result enum;
- an allowlisted machine-readable failure code;
- `.dem` or `.dem.zst` source kind where relevant.

Durations and counts are bounded server-side. One Analytics Engine numeric field
always records a count of one so sampled aggregate queries remain correct.

## Never Collect

- SteamID, player names, avatars, or roster identity;
- demo names, content, hashes, map server names, or replay payloads;
- filesystem paths, Steam library paths, output paths, or machine usernames;
- server addresses, IP addresses, request headers, or network identifiers;
- cosmetic inventories, voice data, crosshair codes, or manifest metadata;
- raw error messages, stack traces, logs, command lines, or free-form text;
- hardware serials, Windows account identity, or a stable installation ID.

## Cloudflare Dataset

Use one versioned Analytics Engine dataset such as
`demotracer_product_events_v1`.

- `index1`: daily anonymous identifier;
- `blob1..blob8`: schema, event, app version, channel, language, platform,
  result, and safe code/source kind;
- `double1..double4`: count, duration, item count, and byte count.

Analytics Engine is preferred over D1 because the product needs aggregate time
series rather than mutable user rows or relational joins. R2 remains release
storage and must not become an event lake. Queries use a private Cloudflare API
token with analytics-read permission and always include a bounded time range.

Cloudflare currently documents three-month Analytics Engine retention. The
Worker Free plan and Analytics Engine Free plan each allow 100,000 requests or
data points per day. The desktop should still batch up to 20 events per request
and cap normal delivery at one batch per minute.

## Delivery Phases

1. Approve the consent wording, daily-ID boundary, and event allowlist.
2. Add a locally tested Worker with a staging dataset and no custom domain.
3. Add the disabled-by-default desktop collector and visible Settings control.
4. Verify captured rows contain only the documented columns.
5. Bind `telemetry.detr.site`, enable production ingestion, and update
   `ONLINE_SERVICES.md` before any public build sends data.

References:

- [Workers Analytics Engine](https://developers.cloudflare.com/analytics/analytics-engine/)
- [Analytics Engine limits](https://developers.cloudflare.com/analytics/analytics-engine/limits/)
- [Analytics Engine pricing](https://developers.cloudflare.com/analytics/analytics-engine/pricing/)
- [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/)
