//! Filesystem and HTTP `/v1` transports. Both expose the same `/sim`-relative path vocabulary, so
//! playback code never knows which side of the mirror it is using.

use std::fmt;
use std::fs;
use std::path::PathBuf;
use std::time::Duration;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TransportError {
    pub code: String,
    pub message: String,
}

impl TransportError {
    pub fn new(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
        }
    }
}

impl fmt::Display for TransportError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.code, self.message)
    }
}

impl std::error::Error for TransportError {}

pub trait Transport: Send {
    fn read_text(&mut self, path: &str) -> Result<String, TransportError>;
    fn write_text(&mut self, path: &str, value: &str) -> Result<(), TransportError>;
    fn upload_text(&mut self, path: &str, value: &str) -> Result<(), TransportError>;
    fn discover_vessels(&mut self) -> Result<Vec<String>, TransportError>;
    fn discover_bodies(&mut self) -> Result<Vec<String>, TransportError>;
    fn label(&self) -> String;
}

pub struct FsTransport {
    root: PathBuf,
}

impl FsTransport {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }

    fn list(&self, path: &str) -> Result<Vec<String>, TransportError> {
        let mut values = fs::read_dir(self.root.join(path))
            .map_err(io_error)?
            .filter_map(Result::ok)
            .filter_map(|entry| entry.file_name().into_string().ok())
            .collect::<Vec<_>>();
        values.sort();
        Ok(values)
    }
}

impl Transport for FsTransport {
    fn read_text(&mut self, path: &str) -> Result<String, TransportError> {
        fs::read_to_string(self.root.join(path))
            .map(|value| value.trim_end_matches(['\r', '\n']).to_string())
            .map_err(io_error)
    }

    fn write_text(&mut self, path: &str, value: &str) -> Result<(), TransportError> {
        let mut payload = value.to_string();
        if !payload.ends_with('\n') {
            payload.push('\n');
        }
        fs::write(self.root.join(path), payload).map_err(io_error)
    }

    fn upload_text(&mut self, path: &str, value: &str) -> Result<(), TransportError> {
        fs::write(self.root.join(path), value).map_err(io_error)
    }

    fn discover_vessels(&mut self) -> Result<Vec<String>, TransportError> {
        self.list("vessels/by-id")
    }

    fn discover_bodies(&mut self) -> Result<Vec<String>, TransportError> {
        self.list("bodies")
    }

    fn label(&self) -> String {
        format!("fs:{}", self.root.display())
    }
}

fn io_error(error: std::io::Error) -> TransportError {
    let code = match error.raw_os_error() {
        Some(1) => "EPERM".into(),
        Some(2) => "ENOENT".into(),
        Some(13) => "EACCES".into(),
        Some(16) => "EBUSY".into(),
        Some(22) => "EINVAL".into(),
        Some(28) => "ENOSPC".into(),
        Some(95) => "EOPNOTSUPP".into(),
        Some(110) => "ETIMEDOUT".into(),
        Some(value) => format!("E{value}"),
        None => "EIO".into(),
    };
    TransportError::new(code, error.to_string())
}

pub struct HttpTransport {
    base: String,
    agent: ureq::Agent,
}

impl HttpTransport {
    pub fn new(base: impl Into<String>) -> Self {
        let agent = ureq::AgentBuilder::new()
            .timeout_connect(Duration::from_secs(2))
            .timeout_read(Duration::from_secs(4))
            .build();
        Self {
            base: base.into().trim_end_matches('/').to_string(),
            agent,
        }
    }

    fn get(&self, suffix: &str) -> Result<String, TransportError> {
        match self.agent.get(&self.endpoint_url(suffix)).call() {
            Ok(response) => response
                .into_string()
                .map_err(|error| TransportError::new("EIO", error.to_string())),
            Err(error) => Err(http_error(error)),
        }
    }

    fn post(&self, path: &str, value: &str) -> Result<(), TransportError> {
        match self
            .agent
            .post(&self.field_url(path))
            .set("content-type", "text/plain; charset=utf-8")
            .send_string(value)
        {
            Ok(_) => Ok(()),
            Err(error) => Err(http_error(error)),
        }
    }

    fn endpoint_url(&self, suffix: &str) -> String {
        format!("{}{suffix}", self.base)
    }

    fn field_url(&self, path: &str) -> String {
        format!("{}/fs/{path}", self.base)
    }
}

impl Transport for HttpTransport {
    fn read_text(&mut self, path: &str) -> Result<String, TransportError> {
        self.get(&format!("/fs/{path}"))
            .map(|value| value.trim_end_matches(['\r', '\n']).to_string())
    }

    fn write_text(&mut self, path: &str, value: &str) -> Result<(), TransportError> {
        self.post(path, value)
    }

    fn upload_text(&mut self, path: &str, value: &str) -> Result<(), TransportError> {
        self.post(path, value)
    }

    fn discover_vessels(&mut self) -> Result<Vec<String>, TransportError> {
        parse_vessels(&self.get("/vessels")?)
    }

    fn discover_bodies(&mut self) -> Result<Vec<String>, TransportError> {
        parse_bodies(&self.get("/bodies")?)
    }

    fn label(&self) -> String {
        self.base.clone()
    }
}

fn parse_vessels(value: &str) -> Result<Vec<String>, TransportError> {
    serde_json::from_str(value)
        .map_err(|error| TransportError::new("EINVAL", format!("bad vessel list: {error}")))
}

fn parse_bodies(value: &str) -> Result<Vec<String>, TransportError> {
    let value: serde_json::Value = serde_json::from_str(value)
        .map_err(|error| TransportError::new("EINVAL", format!("bad body list: {error}")))?;
    let array = value
        .as_array()
        .ok_or_else(|| TransportError::new("EINVAL", "body list is not a JSON array"))?;
    let mut ids = array
        .iter()
        .filter_map(|body| body.get("id").and_then(serde_json::Value::as_str))
        .map(str::to_string)
        .collect::<Vec<_>>();
    ids.sort();
    Ok(ids)
}

fn http_error(error: ureq::Error) -> TransportError {
    match error {
        ureq::Error::Status(status, response) => {
            let fallback = format!("HTTP {status}");
            let body = response.into_string().unwrap_or_default();
            if let Ok(value) = serde_json::from_str::<serde_json::Value>(&body) {
                let code = value
                    .get("errno")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or(&fallback)
                    .to_string();
                let message = value
                    .get("message")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or(&body)
                    .to_string();
                TransportError::new(code, message)
            } else {
                TransportError::new(fallback, body)
            }
        }
        ureq::Error::Transport(error) => TransportError::new("ECONN", error.to_string()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn filesystem_fixture_discovers_uploads_and_writes() {
        let dir = tempfile::tempdir().unwrap();
        for path in [
            "vessels/by-id/Hunter",
            "vessels/by-id/Polaris",
            "bodies/Kerth",
            "camera/track",
            "camera",
        ] {
            fs::create_dir_all(dir.path().join(path)).unwrap();
        }
        fs::write(dir.path().join("camera/enabled"), "0\n").unwrap();
        let mut transport = FsTransport::new(dir.path());
        assert_eq!(transport.discover_vessels().unwrap(), ["Hunter", "Polaris"]);
        assert_eq!(transport.discover_bodies().unwrap(), ["Kerth"]);
        transport
            .upload_text("camera/track/take", "{\"shots\":[]}")
            .unwrap();
        transport.write_text("camera/enabled", "1").unwrap();
        assert_eq!(
            fs::read_to_string(dir.path().join("camera/enabled")).unwrap(),
            "1\n"
        );
        assert_eq!(
            transport.read_text("camera/track/take").unwrap(),
            "{\"shots\":[]}"
        );
    }

    #[test]
    fn http_request_shapes_use_aggregate_discovery_and_field_mirror_paths() {
        let transport = HttpTransport::new("http://127.0.0.1:4242/v1/");
        assert_eq!(
            transport.endpoint_url("/vessels"),
            "http://127.0.0.1:4242/v1/vessels"
        );
        assert_eq!(
            transport.endpoint_url("/bodies"),
            "http://127.0.0.1:4242/v1/bodies"
        );
        assert_eq!(
            transport.field_url("camera/track/take"),
            "http://127.0.0.1:4242/v1/fs/camera/track/take"
        );
        assert_eq!(
            transport.field_url("ctl/timed_batch"),
            "http://127.0.0.1:4242/v1/fs/ctl/timed_batch"
        );
        assert_eq!(
            transport.field_url("camera/pose/fov"),
            "http://127.0.0.1:4242/v1/fs/camera/pose/fov"
        );
        assert_eq!(
            parse_vessels("[\"Polaris\",\"Hunter\"]").unwrap(),
            ["Polaris", "Hunter"]
        );
        assert_eq!(
            parse_bodies("[{\"id\":\"Mun\"},{\"id\":\"Kerth\"}]").unwrap(),
            ["Kerth", "Mun"]
        );
    }
}
