# Installable helper packages

Build all helpers with `python scripts/Build-HelperPackages.py`. The default output is ignored `artifacts/helper-packages/0.4.0`; use `--output-dir` to choose another folder. Node.js/npm and Python are needed to build; Pi/OpenCode helper installation needs Node.js. The desktop bridge installer is separate.

| Harness | Package | Installation without an AI |
| --- | --- | --- |
| Pi | LocalPerplexityBridge-pi-helper-0.4.0.zip | Extract all files, run Install.cmd or node Install.mjs; restart Pi or /reload |
| OpenCode | LocalPerplexityBridge-opencode-helper-0.4.0.zip | Extract all files, run Install.cmd or node Install.mjs; restart OpenCode |
| DeepSeek Harness | LocalPerplexityBridge-deepseek-harness-0.1.0.zip | Extract to a permanent folder; Plugins → Add plugin → enter its absolute folder path → Install → Enable now |
| DeepSeek native alternative | dsh-perplexity-bridge-0.1.0.tgz | Plugins → Add plugin → absolute archive path → Install → Enable now |

Pi/OpenCode installers back up an existing helper before replacing it. They do not modify provider configurations or credentials. Each ZIP includes a provider configuration example, README, original MIT license, package version, and the exact helper source. Merge examples into existing configuration; do not replace entire configuration files. Configure a local bridge key and keep the app running. Pi's in-app setup remains an alternative for initial provider configuration.

DeepSeek's README explains saved-credential references and environment fallback. Select the plugin-owned **perplexity-bridge** provider and create a new chat; an older manually configured provider does not gain session tracking by installing the bundle. Extracted local bundles must stay in place while linked by Harness.

Verify downloaded packages against SHA256SUMS.txt. Run `python tests/helper-packages.py <output-folder>` to check archive integrity and SHA-256 hashes, then install Pi/OpenCode helpers twice into isolated test folders to verify replacement and backups. This does not install helpers into the user's active configuration or prove full Harness UI activation.
