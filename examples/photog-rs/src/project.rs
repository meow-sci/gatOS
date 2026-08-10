//! Project persistence. Saving uses a sibling temporary file followed by `rename`, so a crash can
//! leave the old project or the complete new one, never a partially-written JSON document.

use std::fmt;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::model::{Project, PROJECT_VERSION};

#[derive(Debug)]
pub enum ProjectError {
    Io(io::Error),
    Json(serde_json::Error),
    UnsupportedVersion(u32),
    NoParent(PathBuf),
}

impl fmt::Display for ProjectError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(f, "{error}"),
            Self::Json(error) => write!(f, "{error}"),
            Self::UnsupportedVersion(version) => {
                write!(
                    f,
                    "project schema version {version} is unsupported (expected {PROJECT_VERSION})"
                )
            }
            Self::NoParent(path) => write!(f, "{} has no parent directory", path.display()),
        }
    }
}

impl std::error::Error for ProjectError {}

impl From<io::Error> for ProjectError {
    fn from(value: io::Error) -> Self {
        Self::Io(value)
    }
}

impl From<serde_json::Error> for ProjectError {
    fn from(value: serde_json::Error) -> Self {
        Self::Json(value)
    }
}

pub fn load_project(path: impl AsRef<Path>) -> Result<Project, ProjectError> {
    let project: Project = serde_json::from_slice(&fs::read(path)?)?;
    if project.version != PROJECT_VERSION {
        return Err(ProjectError::UnsupportedVersion(project.version));
    }
    Ok(project)
}

pub fn save_project_atomic(path: impl AsRef<Path>, project: &Project) -> Result<(), ProjectError> {
    let path = path.as_ref();
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    fs::create_dir_all(parent)?;

    let stem = path
        .file_name()
        .ok_or_else(|| ProjectError::NoParent(path.to_path_buf()))?
        .to_string_lossy();
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let temp = parent.join(format!(".{stem}.{}.{}.tmp", std::process::id(), nonce));

    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)?;
        serde_json::to_writer_pretty(&mut file, project)?;
        file.write_all(b"\n")?;
        file.sync_all()?;
        replace_file(&temp, path)?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp);
    }
    result
}

#[cfg(not(windows))]
fn replace_file(source: &Path, destination: &Path) -> io::Result<()> {
    fs::rename(source, destination)
}

// std::fs::rename cannot replace an existing destination on Windows. MoveFileExW with
// REPLACE_EXISTING is the platform's same-volume atomic replacement primitive, preserving the
// sibling-temp + rename guarantee for host-side HTTP users as well as in-guest Linux users.
#[cfg(windows)]
fn replace_file(source: &Path, destination: &Path) -> io::Result<()> {
    use std::iter;
    use std::os::windows::ffi::OsStrExt;

    const MOVEFILE_REPLACE_EXISTING: u32 = 0x1;
    const MOVEFILE_WRITE_THROUGH: u32 = 0x8;

    #[link(name = "Kernel32")]
    extern "system" {
        fn MoveFileExW(existing: *const u16, replacement: *const u16, flags: u32) -> i32;
    }

    let source = source
        .as_os_str()
        .encode_wide()
        .chain(iter::once(0))
        .collect::<Vec<_>>();
    let destination = destination
        .as_os_str()
        .encode_wide()
        .chain(iter::once(0))
        .collect::<Vec<_>>();
    // SAFETY: both pointers reference live, NUL-terminated UTF-16 buffers for the duration of the
    // call; flags are the documented MoveFileExW replacement/write-through combination.
    if unsafe {
        MoveFileExW(
            source.as_ptr(),
            destination.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    } == 0
    {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::Project;

    #[test]
    fn project_round_trip_and_atomic_replacement() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("take.photog.json");
        let mut project = Project::new("first");
        save_project_atomic(&path, &project).unwrap();
        assert_eq!(load_project(&path).unwrap(), project);

        project.name = "replacement".into();
        save_project_atomic(&path, &project).unwrap();
        assert_eq!(load_project(&path).unwrap().name, "replacement");
        assert_eq!(fs::read_dir(dir.path()).unwrap().count(), 1);
    }

    #[test]
    fn rejects_unknown_fields_and_wrong_versions() {
        let dir = tempfile::tempdir().unwrap();
        let unknown = dir.path().join("unknown.json");
        fs::write(
            &unknown,
            r#"{"version":1,"name":"x","loop":false,"shots":[],"wat":1}"#,
        )
        .unwrap();
        assert!(matches!(load_project(&unknown), Err(ProjectError::Json(_))));

        let version = dir.path().join("version.json");
        fs::write(
            &version,
            r#"{"version":99,"name":"x","loop":false,"shots":[]}"#,
        )
        .unwrap();
        assert!(matches!(
            load_project(&version),
            Err(ProjectError::UnsupportedVersion(99))
        ));
    }

    #[test]
    fn malformed_numbers_are_rejected() {
        let json = r#"{"version":1,"name":"x","shots":[{"name":"x","duration_s":"five"}]}"#;
        assert!(serde_json::from_str::<Project>(json).is_err());
    }
}
