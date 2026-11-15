# Third-party notices

## Daozai

The optional native desktop island builds
[Fishtail-boy/daozai](https://github.com/Fishtail-boy/daozai/tree/6eb33732f9b2150e02eef1b1f824b0c087a2c5f2)
with the MemoDesk adapter in `integrations/daozai`.
Copyright (c) 2026 小唐的Token and Daozai contributors, MIT License.
The original license and third-party asset notices accompany the island executable.

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
