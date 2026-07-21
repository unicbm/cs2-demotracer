fn validate_windows_icon(path: &std::path::Path) {
    const REQUIRED_SIZES: &[u16] = &[16, 20, 24, 28, 32, 36, 40, 48, 64, 96, 128, 256];

    let bytes = std::fs::read(path).expect("failed to read Windows icon");
    assert!(bytes.len() >= 6, "Windows icon header is truncated");
    assert_eq!(
        u16::from_le_bytes([bytes[0], bytes[1]]),
        0,
        "invalid Windows icon header"
    );
    assert_eq!(
        u16::from_le_bytes([bytes[2], bytes[3]]),
        1,
        "Windows icon is not an ICO file"
    );

    let count = u16::from_le_bytes([bytes[4], bytes[5]]) as usize;
    assert!(
        bytes.len() >= 6 + count * 16,
        "Windows icon directory is truncated"
    );

    let mut sizes = std::collections::BTreeSet::new();
    for index in 0..count {
        let offset = 6 + index * 16;
        let width = if bytes[offset] == 0 {
            256
        } else {
            u16::from(bytes[offset])
        };
        let height = if bytes[offset + 1] == 0 {
            256
        } else {
            u16::from(bytes[offset + 1])
        };
        if width == height {
            sizes.insert(width);
        }
    }

    let missing = REQUIRED_SIZES
        .iter()
        .copied()
        .filter(|size| !sizes.contains(size))
        .collect::<Vec<_>>();
    assert!(
        missing.is_empty(),
        "Windows icon is missing native DPI sizes: {missing:?}"
    );
}

fn main() {
    let manifest_dir = std::path::PathBuf::from(
        std::env::var_os("CARGO_MANIFEST_DIR").expect("Cargo did not provide CARGO_MANIFEST_DIR"),
    );
    let icon_path = manifest_dir.join("icons").join("icon.ico");
    validate_windows_icon(&icon_path);
    println!("cargo:rerun-if-changed={}", icon_path.display());

    let windows = tauri_build::WindowsAttributes::new().window_icon_path(icon_path);
    let attributes = tauri_build::Attributes::new().windows_attributes(windows);
    tauri_build::try_build(attributes).expect("failed to run Tauri build helpers");
}
