fn main() {
    let manifest_dir = std::path::PathBuf::from(
        std::env::var_os("CARGO_MANIFEST_DIR").expect("Cargo did not provide CARGO_MANIFEST_DIR"),
    );
    let icon_path = manifest_dir.join("icons").join("icon.ico");
    println!("cargo:rerun-if-changed={}", icon_path.display());

    let windows = tauri_build::WindowsAttributes::new().window_icon_path(icon_path);
    let attributes = tauri_build::Attributes::new().windows_attributes(windows);
    tauri_build::try_build(attributes).expect("failed to run Tauri build helpers");
}
