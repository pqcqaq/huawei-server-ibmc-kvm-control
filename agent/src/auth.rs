use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use base64::Engine as _;
use rand::RngCore as _;
use subtle::ConstantTimeEq as _;
use zeroize::{Zeroize, Zeroizing};

pub const TOKEN_LENGTH: usize = 32;

pub struct PairingToken(Zeroizing<Vec<u8>>);

impl PairingToken {
    pub fn load(path: &Path) -> Result<Self, AuthError> {
        validate_user_file_mode(path)?;
        let encoded = fs::read_to_string(path)?;
        let decoded = base64::engine::general_purpose::STANDARD.decode(encoded.trim())?;
        if decoded.len() != TOKEN_LENGTH {
            return Err(AuthError::InvalidTokenLength(decoded.len()));
        }
        Ok(Self(Zeroizing::new(decoded)))
    }

    pub fn generate(path: &Path) -> Result<String, AuthError> {
        if path.exists() {
            return Err(AuthError::AlreadyExists(path.to_path_buf()));
        }
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
            set_user_directory_mode(parent)?;
        }
        let mut token = Zeroizing::new(vec![0u8; TOKEN_LENGTH]);
        rand::rng().fill_bytes(&mut token);
        let encoded = base64::engine::general_purpose::STANDARD.encode(token.as_slice());
        fs::write(path, format!("{encoded}\n"))?;
        set_user_file_mode(path)?;
        Ok(encoded)
    }

    pub fn verify(&self, candidate: &[u8]) -> bool {
        candidate.len() == self.0.len() && bool::from(self.0.as_slice().ct_eq(candidate))
    }
}

impl Drop for PairingToken {
    fn drop(&mut self) {
        self.0.zeroize();
    }
}

#[derive(Debug, thiserror::Error)]
pub enum AuthError {
    #[error("pairing token file already exists: {0}")]
    AlreadyExists(PathBuf),
    #[error("pairing token must decode to {TOKEN_LENGTH} bytes, got {0}")]
    InvalidTokenLength(usize),
    #[error("pairing token is not valid base64: {0}")]
    Base64(#[from] base64::DecodeError),
    #[error("pairing token file permissions {0:o} allow access by group or other users")]
    InsecurePermissions(u32),
    #[error("pairing token file operation failed: {0}")]
    Io(#[from] io::Error),
}

#[cfg(unix)]
fn set_user_file_mode(path: &Path) -> io::Result<()> {
    use std::os::unix::fs::PermissionsExt as _;
    fs::set_permissions(path, fs::Permissions::from_mode(0o600))
}

#[cfg(unix)]
fn validate_user_file_mode(path: &Path) -> Result<(), AuthError> {
    use std::os::unix::fs::PermissionsExt as _;
    let mode = fs::metadata(path)?.permissions().mode() & 0o777;
    if mode & 0o077 != 0 {
        return Err(AuthError::InsecurePermissions(mode));
    }
    Ok(())
}

#[cfg(not(unix))]
fn validate_user_file_mode(_path: &Path) -> Result<(), AuthError> {
    Ok(())
}

#[cfg(not(unix))]
fn set_user_file_mode(_path: &Path) -> io::Result<()> {
    Ok(())
}

#[cfg(unix)]
fn set_user_directory_mode(path: &Path) -> io::Result<()> {
    use std::os::unix::fs::PermissionsExt as _;
    fs::set_permissions(path, fs::Permissions::from_mode(0o700))
}

#[cfg(not(unix))]
fn set_user_directory_mode(_path: &Path) -> io::Result<()> {
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generated_token_round_trips_and_rejects_changes() {
        let directory = crate::test_support::TestDirectory::new();
        let path = directory.path().join("token");
        let encoded = PairingToken::generate(&path).unwrap();
        let token = PairingToken::load(&path).unwrap();
        let mut decoded = base64::engine::general_purpose::STANDARD
            .decode(encoded)
            .unwrap();

        assert!(token.verify(&decoded));
        decoded[0] ^= 1;
        assert!(!token.verify(&decoded));
        assert!(!token.verify(&decoded[..31]));
    }

    #[cfg(unix)]
    #[test]
    fn rejects_token_file_accessible_by_other_users() {
        use std::os::unix::fs::PermissionsExt as _;

        let directory = crate::test_support::TestDirectory::new();
        let path = directory.path().join("token");
        PairingToken::generate(&path).unwrap();
        fs::set_permissions(&path, fs::Permissions::from_mode(0o640)).unwrap();

        assert!(matches!(
            PairingToken::load(&path),
            Err(AuthError::InsecurePermissions(0o640))
        ));
    }
}
