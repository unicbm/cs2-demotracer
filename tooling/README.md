# Repository tooling

This area contains maintainer automation rather than product runtime code.

| Path | Responsibility |
| --- | --- |
| [`scripts/`](scripts/) | Validation, packaging, signing, and publishing workflows |
| [`release/`](release/) | Public updater verification material; private keys are never stored here |
| [`cs2-lib-data/`](cs2-lib-data/) | Locked `@ianlucas/cs2-lib` dependency and deterministic cross-runtime econ projection generator |

Run scripts from the repository root so their documented relative paths remain
easy to audit. Release and signing details are in
[the development guide](../docs/DEVELOPMENT.md#packaging).
