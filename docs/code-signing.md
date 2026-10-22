# Code signing with Azure Artifact Signing

Windows **Smart App Control** can block unsigned executables at any time, even ones it allowed before, because unsigned code is judged by cloud reputation. Signing the release binaries with a publicly trusted certificate fixes this. Signing also gives SmartScreen a baseline reputation.

This project signs releases with **Azure Artifact Signing** (formerly *Trusted Signing*; the service is the same). The certificates it issues are short-lived, so every signature is timestamped.

> Facts below were checked against Microsoft Learn in October 2026. Prices and eligibility can change; confirm them on the [Quickstart](https://learn.microsoft.com/azure/artifact-signing/quickstart) and [FAQ](https://learn.microsoft.com/azure/artifact-signing/faq) pages.

## Costs and eligibility

| Item | Details |
|---|---|
| Basic plan | $9.99 per month, including 5,000 signatures per month |
| Premium plan | $99.99 per month, including 100,000 signatures per month |
| Billing | Starts when the account is created. The first month is charged in full. |
| Subscription | Pay-as-you-go or enterprise agreement. Free, trial and sponsored subscriptions are not supported. |
| Public Trust, organizations | Available in the US, Canada, EU, UK, Australia, New Zealand, Japan, **South Korea**, Singapore, Switzerland, Norway and Israel |
| Public Trust, individual developers | US and Canada only |
| Identity validation time | 1–20 business days; cannot be expedited |

## One-time setup (Azure)

1. **Register the resource provider and install the Azure CLI extension.**
   ```powershell
   az login
   az account set -s <subscription-id>
   az provider register --namespace Microsoft.CodeSigning
   az extension add --name artifact-signing
   ```
2. **Create the signing account.** Korea Central uses the endpoint `https://krc.codesigning.azure.net`.
   - The name must be 3–24 alphanumeric characters, start with a letter, and be globally unique.
   ```powershell
   az group create --name rg-codesigning --location koreacentral
   az artifact-signing create -n <account> -g rg-codesigning -l koreacentral --sku Basic
   ```
3. **Validate the organization's identity.** This step is done in the Azure portal.
   1. Give yourself the **Artifact Signing Identity Verifier** role on the account.
   2. Open *Account → Identity validations → New identity → Organization → Public*.
   3. Enter the following:
      - the legal entity name (this becomes the certificate subject);
      - the website;
      - a primary and a secondary e-mail address on the company domain;
      - the business identifier (for example, the business registration number);
      - the business address;
      - the representative's name exactly as it appears on their ID.
   4. The representative verifies their ID through Microsoft Authenticator.
   5. If Microsoft asks for documents, provide a business registration certificate and proof of domain ownership. Each document must have been issued within the last 12 months.
4. **Create the certificate profile** once the validation status is *Completed*.
   ```powershell
   az artifact-signing certificate-profile create -g rg-codesigning --account-name <account> `
     -n <profile> --profile-type PublicTrust --identity-validation-id <validation-guid>
   ```
5. **Grant signing rights** to the person or service principal that will run the build.
   ```powershell
   az role assignment create --assignee <object-id> --role "Artifact Signing Certificate Profile Signer" `
     --scope "/subscriptions/<sub>/resourceGroups/rg-codesigning/providers/Microsoft.CodeSigning/codeSigningAccounts/<account>/certificateProfiles/<profile>"
   ```

## Signing builds of this project

1. **Install the signing module.** It downloads SignTool and the signing client the first time it runs, and it requires the .NET runtime.
   ```powershell
   Install-Module ArtifactSigning -Scope CurrentUser
   ```
2. **Create the configuration.** Copy `signing.example.json` to `signing.json` (this file is git-ignored) and fill in your endpoint, account name and certificate profile name.
3. **Sign in to Azure.** Either:
   - run `az login`; or
   - for automated builds, set `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET` for a service principal that has the signer role.
4. **Build.**
   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1 -RequireSigning
   ```
   The build does the following:
   - signs `LegionKbdRgb.exe` **before** embedding it in the installer, because the installer writes that exact file to disk;
   - signs the installer itself, which is also installed as `uninstall.exe`;
   - verifies both signatures and stops if either check fails.
5. **Check the result** if you want to inspect the signature yourself.
   ```powershell
   Get-AuthenticodeSignature dist\LegionKbdRgb-Setup-*.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
   ```

## Notes

- **Always timestamp.** Artifact Signing certificates are valid for only about three days. A signature without a timestamp stops validating once the certificate expires. `build.ps1` always timestamps with `http://timestamp.acs.microsoft.com`.
- **SmartScreen reputation.** It builds per file over time, so a brand-new release may still show a warning for a while. If the warning persists, files can be submitted to Microsoft at <https://www.microsoft.com/wdsi>.
