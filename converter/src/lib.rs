pub mod api;
pub mod browser_analysis;
pub mod demo_id;
pub mod demo_reader;
pub mod export;
pub mod model;
pub mod nade_export;
pub mod nade_library;
pub mod pool;
pub mod quality;
pub mod rec_writer;
pub mod synthesis;

#[cfg(feature = "gui")]
pub mod gui;

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
