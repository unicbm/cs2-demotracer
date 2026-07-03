mod analysis;
pub mod api;
pub mod browser_analysis;
pub mod demo_id;
pub mod demo_reader;
pub mod export;
#[cfg(feature = "gui")]
pub mod gui;
pub mod model;
mod nade;
pub mod nade_export;
pub mod nade_library;
pub mod pool;
pub mod quality;
pub mod rec_writer;
mod replay;
pub mod synthesis;
pub mod validate;
pub mod voice_export;
mod workflows;

pub mod dtr {
    pub use crate::model::{
        Cs2Rec, Cs2RecHeader, MovementSnapshot, ReplayProjectile, ReplayTick, SubtickMove,
        DTR_FORMAT_VERSION,
    };
    pub use crate::rec_writer::{read_rec_file, write_rec_file};
}

pub mod prelude {
    pub use crate::api::{
        build_nade_library, build_nade_library_with_progress, export_nade_clips_from_demo_path,
        export_nade_clips_from_parsed, read_nade_library_manifest, read_nade_manifest,
        read_nade_map_manifest, NadeClipExportRequest, NadeContextOptions, NadeDedupeOptions,
        NadeLibraryDemoStatus, NadeLibraryExportRequest, NadeLibraryProgress,
    };
    pub use crate::model::{ProjectileEffectSource, ProjectileKind, Side, SubtickMode};
    pub use crate::nade_export::{
        NadeClip, NadeExportReport, NadeManifest, NadePhase, NadeTimeBucket, NadeTiming,
    };
}

pub type Result<T> = std::result::Result<T, Error>;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O error for {path}: {source}")]
    Io {
        path: String,
        #[source]
        source: std::io::Error,
    },
    #[error("invalid .dtr: {0}")]
    InvalidRec(String),
    #[error("invalid demo data: {0}")]
    InvalidDemo(String),
    #[error("demoparser error: {0}")]
    Parser(String),
    #[error("JSON error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("this build was compiled without the `{0}` feature")]
    FeatureDisabled(&'static str),
}

pub fn io_error(path: impl AsRef<std::path::Path>, source: std::io::Error) -> Error {
    Error::Io {
        path: path.as_ref().display().to_string(),
        source,
    }
}
