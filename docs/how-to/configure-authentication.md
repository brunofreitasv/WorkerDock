# Authentication and access policies

| Deployment configuration | WorkerDock.Read | WorkerDock.Manage |
| --- | --- | --- |
| No authentication/role configuration | Anonymous read | Anonymous management |
| Entra with no required management role | Any valid authenticated user | Any valid authenticated user |
| Entra with a required management role | Any valid authenticated user | Exact configured role claim/value |
| Partial/invalid authentication, missing credential or role without authentication | Startup error | Startup error |

`GET /api/session` returns only `{authenticated,canRead,canManage,mode,language,timeZone}`; modes are `Anonymous`, `Authenticated`, `RoleProtected`. Personal preferences are local language/time-zone values, initially English/UTC. The English and Brazilian Portuguese UI translates access status, navigation, validation and dates. React capabilities control presentation; API policies enforce access.

`/api/dashboard`, `/api/monitoring`, and `/api/audit` require Read and return their available SQL projections; an unavailable store is reported as unavailable rather than as empty data. Both `GET /api/settings` and `PUT /api/settings` require Manage; a viewer receives 403 even by direct API request. Settings PUT also requires the quoted GET ETag in `If-Match` and reports 412 for missing/malformed/stale versions. The Control Center does not grant access to tenant APIs.

## Entra configuration

Each deployment uses its own app registration, confidential credential, cookie/key material and fixed tenant database. Example environment configuration (placeholders must be supplied by deployment tooling):

```powershell
$env:Access__Authority='https://login.microsoftonline.com/<directory-guid>/v2.0'
$env:Access__ClientId='<application-client-id>'
$env:Access__ClientSecretReference='TENANT_SIGNIN'
$env:WORKERDOCK_BOOTSTRAP_TENANT_SIGNIN='<deployment-protected-client-secret>'
$env:Access__RoleClaimType='roles'
$env:Access__RequiredManagementRole='WorkerDock.Manager'
$env:DataProtection__KeyDirectory='C:\ProgramData\WorkerDock\tenant-example\keys'
```

To allow every valid signed-in user to manage, omit `Access__RequiredManagementRole`. For anonymous mode, omit all three bootstrap fields and the required role; network restrictions remain the deployment's responsibility. Do not set empty placeholder fields. `common`, `organizations` and `consumers` authorities are rejected: configure the exact allowed directory issuer and audience, whether deployments share an Entra directory or have independent directories. Signature, expiration, configured issuer and configured audience must validate. Discovery metadata cannot expand the allowed issuer. Role values are case-sensitive; map a custom claim with `Access__RoleClaimType` when required.

Credential references accept only uppercase letters, digits and underscores (1–128 characters). The bootstrap reference resolves **only** the deployment environment variable `WORKERDOCK_BOOTSTRAP_<reference>`. It never reads a tenant database, job vault, browser value or connection profile. Missing/invalid options fail with `AccessConfigurationInvalid`; safe validation codes contain no names, secret values or raw provider errors. Deployment tools must inject credentials without persisting them in source or command history. OIDC bootstrap credential lookup is separate from the job-template `ISecretVault` facade.

Register the web redirect URI `https://<deployment-host>/signin-oidc` and signed-out callback `https://<deployment-host>/signout-callback-oidc` on that application registration. GET `/auth/signin` invokes framework OIDC authorization code + S256 PKCE with correlation/nonce validation. `SaveTokens=false`; no identity/access/refresh token is retained in the authentication ticket or sent in session/configuration DTOs or browser storage. The cookie contains only protected claims/session properties. Auth cookies are host-only `__Host-WorkerDock.Auth`, Secure, HttpOnly, SameSite=Lax, eight-hour absolute lifetime with sliding expiry disabled.

## Browser mutation and sign-out contract

All mutating `/api/*` requests, including anonymous requests, and POST `/auth/signout` require the header `X-WorkerDock-CSRF` returned by GET `/api/antiforgery`, its secure HttpOnly host-only cookie, and an exact same-origin `Origin`. Missing token/origin, another origin or a token from another browser cookie is rejected with safe 400 `AntiforgeryFailed`. The client uses relative same-origin API URLs and `credentials:same-origin`; there is no broad CORS policy. API/auth routes require HTTPS and return 400 `HttpsRequired` over plaintext. GET sign-out returns 405.

The browser sends CSRF-protected POST `/auth/signout`. Framework cookie and OIDC sign-out handlers clear the local identity and produce the provider logout URL, returned as `{redirectUrl}` for top-level browser navigation. The URL includes framework-protected authentication-property state, which the signed-out callback validates to restore the fixed local return URI `/`. The frontend navigates this URL unchanged. Unsolicited provider logout at `/signout-oidc` is disabled; cross-site requests to that path cannot clear the session. It contains no `id_token_hint` because tokens are not retained. Provider/global SSO session behavior is subject to Entra; verify it during the live deployment smoke check. API/session/auth responses are `no-store`.

Each anonymous browser receives a server-generated opaque subject in an authenticated/encrypted `__Host-WorkerDock.Session` cookie (Secure, HttpOnly, SameSite=Strict, eight-hour server-validated expiry). The subject is stable within that browser session and differs across browsers. The server supplies actor/authentication mode and a fresh correlation ID for each mutation; caller-supplied identity/correlation values do not override audit identity. Anonymous management remains deliberately public to reachable clients.

## Protected keys and IIS

Outside Development, startup requires Windows and an absolute `DataProtection__KeyDirectory`; otherwise it returns `DataProtectionConfigurationInvalid`. Keys in that directory are protected with Windows DPAPI for the hosting account and an application discriminator bound to the deployment's tenant instance ID. During installation, provision a dedicated directory for each deployment, restrict ACLs to the hosting identity and administrators, and back it up with the application. Keep the IIS application identity consistent across restarts. Do not share another deployment's directory or credentials. DPAPI user protection is a single Windows identity/machine deployment choice; a later multi-node deployment must explicitly provision an appropriate certificate/key protection design instead of sharing these files across machines blindly.

Require TLS in IIS and configure the ASP.NET Core Hosting Bundle. If a reverse proxy terminates TLS, explicitly configure trusted forwarding at deployment; do not trust arbitrary forwarded headers. Liveness/readiness exposure has separate network restrictions. Frontend artifacts, fonts and third-party notices are bundled locally in `wwwroot`; Node is not a production runtime dependency.

## Verification and live Entra check

`dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~AccessPolicyTests` exercises the real OIDC/cookie handlers against a locally signed test issuer, including PKCE, viewer/manager/no-role cases, custom role claim, wrong issuer/audience/signature, expired identity, hostile discovery issuer, ticket token absence, direct endpoint denials, CSRF/origin/session checks, per-browser server actors and protected logout. It does not require an external tenant. Frontend tests cover viewer routes, hidden management, translated anonymous mode, local preferences, failure/retry presentation, focus and versioned CSRF settings saves.

Before release, verify the live Entra/IIS flow: exact directory/app callback registration; successful sign-in; expected no-role/viewer/manager/custom-role capabilities; direct endpoint 401/403 checks; rejection of a token issued for another directory/application; TLS/cookie attributes; eight-hour expiry; protected POST sign-out and provider return; no IdP token in storage/session DTOs; protected key persistence under the production IIS identity across restart; anonymous mode only behind intended network restrictions. This live check and real SQL verification are still pending; fixture and memory tests do not substitute for them.
