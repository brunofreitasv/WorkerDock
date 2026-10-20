# Task 2: Integrate Help into tenant navigation

## Result

- Added Help to Read-session navigation, including active state for `#/help:<slug>` deep links.
- Added localized topic titles, contextual Help links where the manifest has a matching topic, and recovery for unknown or malformed topic routes.
- Preserved the Help topic hash while switching languages and retained role-based blocking of manager-only guide destinations.

## TDD and verification

- RED: focused Vitest run failed on missing invalid-topic recovery, Help navigation, active state, contextual link, and deep-link locale behavior.
- GREEN: `npx vitest run apps/tenant/src/help/HelpPage.test.tsx apps/tenant/src/auth/SessionProvider.test.tsx` — 21 tests passed.
- `npm run build --workspace @workerdock/tenant` — passed.

## Scope

Task 2 files only; unrelated dirty files were left unchanged.
