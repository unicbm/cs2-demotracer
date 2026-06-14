# Vendored demoparser notes

This repository vendors the minimal Rust crates used by the converter:

- `src/parser`
- `src/csgoproto`

Large upstream test demos, Node/WASM packages, and the full GameTracking-CS2 checkout are intentionally excluded.

The vendored build scripts use the generated `protobuf.rs` and `maps.rs` files already present in this tree, so normal converter builds do not clone GameTracking-CS2 or rewrite vendored source files.
