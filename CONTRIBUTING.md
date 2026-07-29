# Contributing to DemoTracer

Bug reports, compatibility findings, documentation improvements, tests, and
focused code changes are welcome.

## Before Opening a Change

- Use an issue or discussion first for .dtr, manifest, native ABI, companion
  API, packaging, or cross-component contract changes.
- Keep pull requests narrow and explain the runtime path or user-visible problem.
- Do not include raw demos, replay archives, local paths, logs, credentials,
  signing material, private server configuration, or user data.
- Preserve third-party attribution and avoid unrelated vendor rewrites.

## Validation

Run the checks relevant to your change. The complete sequence is documented in
[Development](docs/DEVELOPMENT.md) and summarized in [AGENTS.md](AGENTS.md).

At minimum, run git diff --check and report any check that could not be run.
Windows x64 is the maintained release target.

## License

Unless a file states otherwise, contributions are accepted under
AGPL-3.0-only. By submitting a contribution, you confirm that you have the
right to provide it under that license. Third-party files and datasets retain
their existing licenses.

Modified distributions must not imply endorsement or official-build status;
see [Trademark and Official Build Policy](TRADEMARKS.md).
