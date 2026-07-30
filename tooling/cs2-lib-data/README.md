# cs2-lib data projection

DemoTracer does not maintain an item-ID registry. The exact
`@ianlucas/cs2-lib` dependency in this directory is the only upstream source
for the generated runtime econ data shared by the Rust converter and the C#
playback plugin.

```powershell
npm.cmd ci --ignore-scripts
npm.cmd run check
```

To refresh the catalog, update the exact dependency and `package-lock.json`,
review the upstream changes, and run:

```powershell
npm.cmd run generate
```

Commit the lockfile and `shared/econ/cs2-lib-econ-index.v1.json` together. Do
not hand-edit the generated JSON or add local fallback item identifiers.
