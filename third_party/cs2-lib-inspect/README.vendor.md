# cs2-lib-inspect attribution

`converter/src/inspect_link.rs` ports the CS2 preview protobuf field layout,
native leading byte, xCRC calculation, and 300-character URL fallback behavior
from
[`ianlucas/cs2-lib-inspect`](https://github.com/ianlucas/cs2-lib-inspect).
Its conditional NameTag field follows the inspect-compatible grammar from
[`ianlucas/cs2-lib`](https://github.com/ianlucas/cs2-lib); incompatible demo
custom names remain exported evidence but are omitted from the preview payload
so they cannot invalidate the whole synthetic item.

DemoTracer wraps the resulting payload in Steam's current CS2 launch URI,
`steam://run/730/en/+csgo_econ_action_preview%20<payload-hex>`.

Upstream reference commit inspected for this port:
`c3638890ecea3c97a4c2b7276e140b4a26abc882`.

NameTag compatibility reference commit:
`e8057c583e89d6b7a37f27e1cb7ebdbe94dd6238`.

No upstream TypeScript or generated protobuf source is vendored. The retained
algorithm attribution and upstream MIT license are included here.
