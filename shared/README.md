# Shared contracts and data

This area contains small, versioned artifacts consumed across product
boundaries.

| Path | Responsibility |
| --- | --- |
| [`contracts/`](contracts/) | Desktop/server release and ABI compatibility contracts |
| [`econ/`](econ/) | Generated compact econ metadata used by conversion and playback |

Keep this directory limited to artifacts that genuinely have multiple
consumers. Product-specific source belongs under `desktop/` or `server/`.
