# Building installers on Linux and macOS

`fluentsetup` runs on Windows, Linux and macOS. On every system it turns the same project into the same Windows
`Setup.exe`: the installer runtime inside it (`FluentSetup.Stub.exe` and the WinUI front end) is prebuilt for Windows
and only copied, and the icon and version resources are written by managed code, not by Windows APIs. Testing the
result still needs Windows.

## Getting the CLI

- **Release archive**: `fluentsetup-<version>-linux-x64.tar.gz` or `fluentsetup-<version>-osx-arm64.tar.gz` from the
  [releases](https://github.com/TIANLI0/FluentSetup/releases), checked against `SHA256SUMS.txt`. Self-contained (no
  .NET needed), with the installer runtime in `runtime/` next to the executable:

  ```sh
  mkdir -p ~/fluentsetup && tar -xzf fluentsetup-1.2.0-linux-x64.tar.gz -C ~/fluentsetup
  ~/fluentsetup/fluentsetup --version
  ```

  On macOS a browser download is quarantined; clear it with `xattr -dr com.apple.quarantine ~/fluentsetup`.
- **GitHub Actions**: `uses: TIANLI0/FluentSetup@v1` works on Linux x64 (`ubuntu-*`) and Apple silicon (`macos-*`)
  runners too (FluentSetup 1.2.0 and later) and puts `fluentsetup` on `PATH`.
- **From source**: with the .NET 10 SDK, `dotnet build src/CLI -c Release` builds the CLI anywhere, but the installer
  runtime is built on Windows (Visual Studio). Take the `runtime/` folder of a release archive and pass it with
  `build --runtime <dir>` or the `FLUENTSETUP_RUNTIME` environment variable.

`update` (installing a newer FluentSetup) works on Windows only; elsewhere `update --check` reports the new version and
you download it yourself.

## What is different

Nothing in the package depends on the build machine. The builder applies Windows rules on every system:

| Topic | Behaviour |
|---|---|
| Paths in the project | `\` and `/` both separate folders, so `"source": "bin\\Release\\publish"` written on Windows works on Linux. Relative paths are relative to the project file. Absolute Windows paths (`C:\...`) do not exist on Linux and macOS. |
| File names | Installed names follow Windows rules: names that differ only in case are the same file (FS1109), `\ : * ? " < > \|`, control characters, trailing dots or spaces and device names such as `CON` or `nul.txt` are rejected (FS1107). A file whose Linux name contains `\` is rejected rather than turned into a folder. |
| Links | Symbolic links are skipped with a warning (FS1112), as junctions and symbolic links are on Windows. |
| Attributes | A file without write permission is installed read-only. A leading dot does not make a file hidden (only the Windows hidden attribute does). Unix permissions and owners are not stored. |
| Output name | `output.fileName` placeholders drop the characters Windows forbids in file names, whatever the build system allows. |
| Signing | `signtool` and `trustedSigning` need Windows (FS1519); use the `command` provider. |

## Signing

The [`command` provider](project-file.md#command) runs any Authenticode signing tool without a shell. FluentSetup
calls it for Setup.exe (and for the `uninstall.exe` it writes, with `signUninstaller`), then checks that the file is
signed. Secrets come from the environment (CI secrets), never from the project file or the command line.

### osslsigncode

`apt install osslsigncode` (Debian, Ubuntu) or `brew install osslsigncode`. It cannot sign in place, so it writes to
`{output}`. The `.pfx` password is read from a file the CI job writes from a secret (and that is never committed):

```json
"signing": {
  "enabled": true,
  "provider": "command",
  "command": {
    "run": "osslsigncode",
    "arguments": ["sign", "-pkcs12", "secrets/codesign.pfx", "-readpass", "secrets/pfx-password.txt", "-h", "sha256",
                  "-n", "{description}", "-ts", "{timestampUrl}", "-in", "{file}", "-out", "{output}"]
  }
}
```

With a PEM certificate and key instead of a `.pfx`: `"-certs", "secrets/cert.pem", "-key", "secrets/key.pem"`.
osslsigncode also signs with keys in an HSM or smart card through PKCS#11 (`-pkcs11module`, `-key "pkcs11:..."`).

### jsign

[jsign](https://ebourg.github.io/jsign/) (Java) signs in place and reads passwords and tokens from environment
variables (`env:NAME`). It supports `.pfx` files, cloud key stores and HSMs:

```json
"command": {
  "run": "jsign",
  "arguments": ["--storetype", "PKCS12", "--keystore", "secrets/codesign.pfx", "--storepass", "env:PFX_PASSWORD",
                "--alg", "SHA-256", "--tsaurl", "{timestampUrl}", "--name", "{description}", "{file}"]
}
```

Azure Artifact Signing (formerly Trusted Signing) from Linux or macOS, with an access token from
`az account get-access-token --resource https://codesigning.azure.net --query accessToken -o tsv` in `AZURE_TOKEN`:

```json
"timestampUrl": "http://timestamp.acs.microsoft.com",
"command": {
  "run": "jsign",
  "arguments": ["--storetype", "TRUSTEDSIGNING", "--keystore", "eus.codesigning.azure.net", "--storepass", "env:AZURE_TOKEN",
                "--alias", "<account>/<certificate profile>", "--tsaurl", "{timestampUrl}", "--name", "{description}", "{file}"]
}
```

Other jsign store types cover Azure Key Vault, AWS KMS, Google Cloud KMS, DigiCert ONE and others. Without the
`jsign` package, run the jar: `"run": "java", "arguments": ["-jar", "tools/jsign.jar", ...]`.

### Other tools

- **AzureSignTool** (Azure Key Vault; Windows): `"run": "AzureSignTool", "arguments": ["sign", "-kvu", "https://<vault>.vault.azure.net", "-kvc", "<certificate>", "-kvm", "-fd", "sha256", "-tr", "{timestampUrl}", "-td", "sha256", "-d", "{description}", "{file}"]`
  (`-kvm` uses the machine's managed identity; avoid `-kvs` with a secret on the command line).
- **SignPath** or any service with its own client: a script that takes `{file}` (and `{output}`), e.g.
  `"run": "pwsh", "arguments": ["-NoProfile", "-File", "tools/sign.ps1", "{file}", "{output}"]`, and reads its API
  token from the environment.

Check a signed package with `fluentsetup inspect Setup.exe --verify` (it reports `signed: yes` and verifies every
stream); `osslsigncode verify -in Setup.exe` checks the signature itself.

## Example: GitHub Actions on Linux

```yaml
jobs:
  installer:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: TIANLI0/FluentSetup@v1
      - run: sudo apt-get install -y osslsigncode
      - name: Certificate from secrets
        env:
          PFX_BASE64: ${{ secrets.CODESIGN_PFX }}
          PFX_PASSWORD: ${{ secrets.CODESIGN_PASSWORD }}
        run: |
          mkdir -p installer/secrets
          printf '%s' "$PFX_BASE64" | base64 -d > installer/secrets/codesign.pfx
          printf '%s' "$PFX_PASSWORD" > installer/secrets/pfx-password.txt
      - run: fluentsetup build installer/fluentsetup.json -D Version=${GITHUB_REF_NAME#v}
      - run: fluentsetup inspect installer/dist/*.exe --verify
      - uses: actions/upload-artifact@v7
        with:
          name: setup
          path: installer/dist/*.exe
```

FluentSetup's own CI does the same with the DemoApp sample: a Linux job builds and signs the package (osslsigncode,
throw-away certificate), and a Windows job installs and uninstalls it (see `.github/workflows/ci.yml`).
