//! Pure project/compiler APIs and live transport/playback support for `photog-rs`.

pub mod compiler;
pub mod model;
pub mod playback;
pub mod project;
pub mod transport;

pub use compiler::{
    compile_project, validate_project, CompileRequest, CompiledTake, ValidationError,
};
pub use model::{
    Aim, AimUp, CameraFrame, Ease, EaseKind, Lens, Project, Projection, Range, Shot, ShotKind,
    SubjectRef, Vec3,
};
pub use playback::{LiveStatus, PlaybackController, PlaybackError, PlaybackOptions};
pub use project::{load_project, save_project_atomic, ProjectError};
pub use transport::{FsTransport, HttpTransport, Transport, TransportError};
