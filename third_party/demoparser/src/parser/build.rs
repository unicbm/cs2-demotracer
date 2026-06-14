use std::io::Result;

fn main() -> Result<()> {
    println!("cargo::rerun-if-changed=../csgoproto/src/protobuf.rs");
    println!("cargo::rerun-if-changed=../csgoproto/src/maps.rs");
    Ok(())
}
