# Third-party notices

## LockerIt

The AES-GCM payload adapter in `MemoDesk.Vault.Infrastructure/VaultCrypto.cs`
is adapted from `src/Lockerit.Core/Security/AesGcmVaultCipher.cs` in
[viamus/locker-it](https://github.com/viamus/locker-it), commit
[`47658a1761a4b4c144bbdffac6fb1cc964bb3f38`](https://github.com/viamus/locker-it/commit/47658a1761a4b4c144bbdffac6fb1cc964bb3f38).

Copyright (c) 2026 LockerIt contributors. Licensed under the MIT License;
the complete notice is in `third-party/LockerIt-LICENSE.txt` and is copied
to the application's `ThirdParty` directory on build and publish.

MemoDesk modifications: bounded binary payloads, identity and revision binding,
separate metadata and secret payloads, and sanitized error handling. The vault
business model, master-password envelope, database, backup format and WPF UI
are MemoDesk implementations. No LockerIt Windows-password collection,
DPAPI-only unlock path, application shell or TOTP authentication flow is included.

NuGet dependencies retain the licenses supplied by their respective packages.
This notice does not assign a distribution license to MemoDesk itself.
