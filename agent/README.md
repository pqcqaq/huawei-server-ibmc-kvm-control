# iBMC KVM Linux Agent

The Rust Agent exposes a logged-in X11 desktop to the Avalonia client. It does not provide firmware, pre-boot, power, shell, file-transfer, clipboard, or virtual-media access.

## Build

```bash
cargo build --manifest-path agent/Cargo.toml --release
install -Dm755 agent/target/release/ibmc-kvm-agent ~/.local/bin/ibmc-kvm-agent
```

## Initialize

Create a self-signed certificate, private key, and random 256-bit pairing token:

```bash
ibmc-kvm-agent init --certificate-name "$(hostname)"
```

Record the printed SHA-256 fingerprint and pairing token. The private key and token are stored under `~/.config/ibmc-kvm-agent` with user-only permissions. Running `init` again never overwrites existing credentials.

## Run

For an interactive test in the active X11 session:

```bash
ibmc-kvm-agent serve --bind 0.0.0.0:7443 --display "$DISPLAY"
```

For a user service:

```bash
install -Dm644 agent/packaging/ibmc-kvm-agent.service \
  ~/.config/systemd/user/ibmc-kvm-agent.service
cat > ~/.config/ibmc-kvm-agent/environment <<EOF
DISPLAY=$DISPLAY
XAUTHORITY=$XAUTHORITY
EOF
chmod 600 ~/.config/ibmc-kvm-agent/environment
systemctl --user daemon-reload
systemctl --user enable --now ibmc-kvm-agent.service
```

Allow TCP port `7443` only from trusted management networks. The server accepts one controller at a time, requires TLS, and rejects invalid pairing tokens before starting capture or input. It also refuses to start when the private key or pairing-token file is accessible by group or other users.

## Client

Select `Linux Agent` on the login screen, enter `HOST:7443`, and paste the pairing token. Confirm that the displayed certificate fingerprint matches the value printed by `init`. Persisting the certificate stores only the public certificate in the encrypted desktop trust store.

For an unattended client smoke test, pass the pairing-token file rather than the token itself:

```bash
chmod 600 /path/to/pairing-token
IbmcKvm.Desktop \
  --direct-agent=HOST:7443 \
  --agent-token-file=/path/to/pairing-token \
  --agent-fingerprint=<SHA256>
```

All three options are required. The client validates the SHA-256 fingerprint before authentication and rejects a Unix token file readable or writable by group or other users.
