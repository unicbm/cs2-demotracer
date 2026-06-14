use std::io::Result;

fn main() -> Result<()> {
    println!("cargo::rerun-if-changed=src/protobuf.rs");
    Ok(())
}
