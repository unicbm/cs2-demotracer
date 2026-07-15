# cs2-lib-inspect attribution

`converter/src/inspect_link.rs` ports the CS2 preview protobuf field layout,
native leading byte, xCRC calculation, Steam URL prefix, and 300-character URL
fallback behavior from
[`ianlucas/cs2-lib-inspect`](https://github.com/ianlucas/cs2-lib-inspect).

Upstream reference commit inspected for this port:
`c3638890ecea3c97a4c2b7276e140b4a26abc882`.

No upstream TypeScript or generated protobuf source is vendored. The retained
algorithm attribution and upstream MIT license are included here.
