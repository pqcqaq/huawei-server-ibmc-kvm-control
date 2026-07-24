use std::io;
use std::path::Path;
use std::sync::Arc;

use rustls::ServerConfig;
use rustls::pki_types::{CertificateDer, PrivateKeyDer, pem::PemObject as _};
use tokio_rustls::TlsAcceptor;

pub fn load_acceptor(certificate_path: &Path, key_path: &Path) -> Result<TlsAcceptor, TlsError> {
    validate_private_key_mode(key_path)?;
    let certificates =
        CertificateDer::pem_file_iter(certificate_path)?.collect::<Result<Vec<_>, _>>()?;
    if certificates.is_empty() {
        return Err(TlsError::NoCertificates);
    }

    let key = PrivateKeyDer::from_pem_file(key_path)?;
    let config = ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(certificates, key)?;
    Ok(TlsAcceptor::from(Arc::new(config)))
}

#[derive(Debug, thiserror::Error)]
pub enum TlsError {
    #[error("TLS file operation failed: {0}")]
    Io(#[from] io::Error),
    #[error("TLS PEM file is invalid: {0}")]
    Pem(#[from] rustls::pki_types::pem::Error),
    #[error("TLS certificate file does not contain a certificate")]
    NoCertificates,
    #[error("TLS private-key file does not contain a supported key")]
    NoPrivateKey,
    #[error("TLS private-key permissions {0:o} allow access by group or other users")]
    InsecurePrivateKeyPermissions(u32),
    #[error("TLS configuration is invalid: {0}")]
    Rustls(#[from] rustls::Error),
}

#[cfg(unix)]
fn validate_private_key_mode(path: &Path) -> Result<(), TlsError> {
    use std::os::unix::fs::PermissionsExt as _;
    let mode = std::fs::metadata(path)?.permissions().mode() & 0o777;
    if mode & 0o077 != 0 {
        return Err(TlsError::InsecurePrivateKeyPermissions(mode));
    }
    Ok(())
}

#[cfg(not(unix))]
fn validate_private_key_mode(_path: &Path) -> Result<(), TlsError> {
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_generated_certificate_and_private_key() {
        let root = crate::test_support::TestDirectory::new();
        let directory = root.path().join("agent");
        crate::config::initialize(&directory, "localhost").unwrap();

        load_acceptor(
            &directory.join("agent-cert.pem"),
            &directory.join("agent-key.pem"),
        )
        .unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn rejects_private_key_accessible_by_other_users() {
        use std::fs;
        use std::os::unix::fs::PermissionsExt as _;

        let root = crate::test_support::TestDirectory::new();
        let directory = root.path().join("agent");
        crate::config::initialize(&directory, "localhost").unwrap();
        let key_path = directory.join("agent-key.pem");
        fs::set_permissions(&key_path, fs::Permissions::from_mode(0o640)).unwrap();

        assert!(matches!(
            load_acceptor(&directory.join("agent-cert.pem"), &key_path),
            Err(TlsError::InsecurePrivateKeyPermissions(0o640))
        ));
    }
}
