use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use rcgen::CertifiedKey;
use sha2::{Digest as _, Sha256};

use crate::auth::{AuthError, PairingToken};

pub struct InitializedConfig {
    pub directory: PathBuf,
    pub pairing_token: String,
    pub certificate_fingerprint: String,
}

pub fn default_config_directory() -> Result<PathBuf, ConfigError> {
    if let Some(value) = std::env::var_os("XDG_CONFIG_HOME") {
        return Ok(PathBuf::from(value).join("ibmc-kvm-agent"));
    }
    let home = std::env::var_os("HOME").ok_or(ConfigError::MissingHome)?;
    Ok(PathBuf::from(home).join(".config/ibmc-kvm-agent"))
}

pub fn initialize(
    directory: &Path,
    certificate_name: &str,
) -> Result<InitializedConfig, ConfigError> {
    if certificate_name.trim().is_empty() || certificate_name.len() > 253 {
        return Err(ConfigError::InvalidCertificateName);
    }
    fs::create_dir_all(directory)?;
    set_directory_mode(directory)?;
    let certificate_path = directory.join("agent-cert.pem");
    let key_path = directory.join("agent-key.pem");
    let token_path = directory.join("pairing-token");
    if certificate_path.exists() || key_path.exists() || token_path.exists() {
        return Err(ConfigError::AlreadyInitialized(directory.to_path_buf()));
    }

    let CertifiedKey { cert, signing_key } =
        rcgen::generate_simple_self_signed(vec![certificate_name.to_owned()])?;
    fs::write(&certificate_path, cert.pem())?;
    fs::write(&key_path, signing_key.serialize_pem())?;
    set_secret_file_mode(&key_path)?;
    let pairing_token = PairingToken::generate(&token_path)?;
    let digest = Sha256::digest(cert.der().as_ref());
    let certificate_fingerprint = digest
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(":");
    Ok(InitializedConfig {
        directory: directory.to_path_buf(),
        pairing_token,
        certificate_fingerprint,
    })
}

#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("HOME is not set; pass --directory explicitly")]
    MissingHome,
    #[error("certificate name is invalid")]
    InvalidCertificateName,
    #[error("agent configuration already exists in {0}")]
    AlreadyInitialized(PathBuf),
    #[error("agent configuration file operation failed: {0}")]
    Io(#[from] io::Error),
    #[error("agent certificate generation failed: {0}")]
    Certificate(#[from] rcgen::Error),
    #[error("agent pairing-token generation failed: {0}")]
    Auth(#[from] AuthError),
}

#[cfg(unix)]
fn set_directory_mode(path: &Path) -> io::Result<()> {
    use std::os::unix::fs::PermissionsExt as _;
    fs::set_permissions(path, fs::Permissions::from_mode(0o700))
}

#[cfg(not(unix))]
fn set_directory_mode(_path: &Path) -> io::Result<()> {
    Ok(())
}

#[cfg(unix)]
fn set_secret_file_mode(path: &Path) -> io::Result<()> {
    use std::os::unix::fs::PermissionsExt as _;
    fs::set_permissions(path, fs::Permissions::from_mode(0o600))
}

#[cfg(not(unix))]
fn set_secret_file_mode(_path: &Path) -> io::Result<()> {
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn initializes_certificate_key_and_token_without_overwriting() {
        let root = crate::test_support::TestDirectory::new();
        let directory = root.path().join("agent");

        let config = initialize(&directory, "localhost").unwrap();

        assert!(directory.join("agent-cert.pem").is_file());
        assert!(directory.join("agent-key.pem").is_file());
        assert!(directory.join("pairing-token").is_file());
        assert_eq!(95, config.certificate_fingerprint.len());
        assert!(matches!(
            initialize(&directory, "localhost"),
            Err(ConfigError::AlreadyInitialized(_))
        ));
    }
}
