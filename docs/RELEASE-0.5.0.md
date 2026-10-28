# Version 0.5.0 — LAN access and repeat-copy keys

Optional LAN mode binds the selected assigned RFC1918 IPv4 address plus loopback. API key authentication is mandatory in LAN mode; exact Host matching, Origin rejection and private IPv4 client filtering remain enforced. The API tab shows detected addresses and a selectable full LAN URL. Firewall rules are not automatically changed. HTTP is unencrypted and intended for a trusted LAN or encrypted tunnel.

New API keys store an authentication hash and a Windows-user DPAPI encrypted recoverable secret. Select a key and Copy selected key after restart as often as needed. Old hash-only keys cannot be recovered; replace them and update clients. Revocation removes authentication and copy access.

Updated Pi/OpenCode helpers and DeepSeek plugin 0.2.0 accept explicitly configured private-network bridge endpoints. Stable harness session IDs remain required; local tools execute on the harness device.

Verification: 166 C# assertions passed including actual HTTP access on the PC's assigned LAN interface, required authentication, immediate revocation, host/origin/private-network policy, encrypted key persistence/retrieval and no plaintext secret in settings. Existing JavaScript helper tests, package install/backup checks and published DeepSeek runtime checks passed. Main WPF build and self-contained installer publish passed. A second physical LAN device, full firewall traversal, new installer execution and v0.5.0 live Perplexity generation remain unverified.
