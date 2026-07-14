# Encrypted Connection Settings

## Scope

The desktop client can optionally restore the address, username, password, shared/exclusive mode, and self-signed-certificate option from the last successful connection. Saving remains opt-in through **记住此连接**.

## Storage design

The application serializes a versioned settings envelope to JSON and encrypts the complete payload with Windows DPAPI using `DataProtectionScope.CurrentUser`. The ciphertext is stored outside the repository at `%LOCALAPPDATA%\IbmcKvm\connection-settings.bin`. A temporary file in the same directory is written first and atomically moved over the destination so an interrupted update does not expose a partial settings file.

DPAPI binds decryption to the signed-in Windows user. The project does not contain an application encryption key, and no credential is written to logs, exceptions, documentation, or project configuration.

## Lifecycle

Startup loads and decrypts a valid supported settings version. A missing, corrupt, oversized, or unsupported file is removed and treated as no saved settings. The application writes settings only after the HTTPS login and KVM connection have both succeeded. A failed connection never overwrites the previously valid file.

Unchecking **记住此连接** deletes the saved file immediately. **清除已保存设置** deletes it and resets the visible connection form. The password control and the temporary local password reference are cleared after use; a manual disconnect reloads the saved credentials when remembering is enabled.

## Verification

Behavior tests cover round-trip restoration, absence of plaintext password bytes, current-user DPAPI decryption, corrupt and unsupported payload recovery, repeated atomic replacement, and idempotent deletion. Release verification also compiles the WPF XAML and runs the complete solution test suite.
