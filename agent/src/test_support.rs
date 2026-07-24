use std::path::{Path, PathBuf};

pub struct TestDirectory(PathBuf);

impl TestDirectory {
    pub fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "ibmc-kvm-agent-test-{}-{}",
            std::process::id(),
            rand::random::<u64>()
        ));
        std::fs::create_dir(&path).expect("create test directory");
        Self(path)
    }

    pub fn path(&self) -> &Path {
        &self.0
    }
}

impl Default for TestDirectory {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for TestDirectory {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
