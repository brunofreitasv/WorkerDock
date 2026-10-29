# SQL Job Profile and Catalog Selectors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace free-text SQL profile ID and catalog fields with a profile selector followed by a selector of that profile's allowed catalogs.

**Architecture:** The tenant job wizard loads the existing connection-profile list when SQL configuration is selected and passes safe display data into the shared SQL template form. The form stores the selected profile ID and catalog in the existing configuration fields; no backend endpoint or database schema is added.

**Tech Stack:** React, TypeScript, MUI, Vitest, Testing Library, existing tenant API client.

**Spec:** `docs/superpowers/specs/2026-10-28-sql-job-profile-and-catalog-selectors-design.md`

## Global Constraints

- Populate catalogs only from the selected profile's configured `allowedCatalogs`; do not enumerate databases from SQL Server.
- Preserve the persisted `profileId` and `catalog` configuration shape.
- Do not expose profile credentials or secret values.
- Keep non-SQL template authoring independent of profile-list loading or failure.
- Preserve existing saved profile/catalog values during edits when the profile is retired or its catalog is no longer allowed; show that the choice is unavailable until replaced.
- Do not add backend endpoints, database objects, or dependencies.

## Review Focus

- Empty profile list or a profile with no allowed catalogs: selectors explain the missing choices and cannot submit a made-up value; test in `SqlTemplateForm.test.tsx`.
- Profile-list request failure: show a SQL-specific error and leave other template forms usable; test in `JobWizard.test.tsx`.
- Profile change: clear the old catalog even if its name happens to occur in both lists; test in `SqlTemplateForm.test.tsx`.
- Retired or missing saved profile during edit: keep its saved ID visible as unavailable until replacement; test in `SqlTemplateForm.test.tsx`.
- Saved catalog removed from that profile's allowlist: preserve and identify the saved value as unavailable instead of silently replacing it; test in `SqlTemplateForm.test.tsx`.

---

### Task 1: Make the SQL template form select profiles and allowed catalogs

**Files:**
- Modify: `frontend/packages/ui/src/forms/TemplateForm.tsx`
- Modify: `frontend/packages/ui/src/forms/SqlTemplateForm.tsx`
- Create: `frontend/packages/ui/src/forms/SqlTemplateForm.test.tsx`
- Modify: `frontend/packages/ui/locales/en.json`
- Modify: `frontend/packages/ui/locales/pt-BR.json`

**Interfaces:**
- Add optional `sqlProfiles` form data containing `status: "loading" | "ready" | "error"` and profile items with `current.profileId`, `current.name`, `current.server`, `current.allowedCatalogs`, and `retired`.
- Keep profile selection in `value.profileId` and catalog selection in `value.catalog`.

- [x] **Step 1: Write failing form tests** for the profile label/options, allowed catalogs for the selected profile, clearing catalog on profile change, empty/error states, and preserving unavailable saved profile/catalog values.
- [x] **Step 2: Run** `npm.cmd --prefix frontend test -- --run packages/ui/src/forms/SqlTemplateForm.test.tsx`. Confirm the new selector assertions fail before implementation.
- [x] **Step 3: Implement the dependent selectors** in `SqlTemplateForm.tsx`; show active profile names plus servers, save the selected profile ID, and offer only its `allowedCatalogs`. Clear catalog when the profile changes. Keep a stale saved profile or catalog visible with unavailable status while editing. Add translated empty/loading/error labels in both locales.
- [x] **Step 4: Run** `npm.cmd --prefix frontend test -- --run packages/ui/src/forms/SqlTemplateForm.test.tsx` and confirm it passes.

### Task 2: Load profiles for SQL job configuration

**Files:**
- Modify: `frontend/apps/tenant/src/features/jobs/JobWizard.tsx`
- Modify: `frontend/apps/tenant/src/features/jobs/JobWizard.test.tsx`

**Interfaces:**
- Consume `GET /api/connection-profiles/` using the existing API client and pass the returned profile list plus its loading state to `TemplateForm` only for `workerdock.sql-server`.
- Preserve SQL profile values already loaded from a saved job, including retired profiles.

- [x] **Step 1: Write failing wizard tests** that selecting SQL loads the profile list and passes it into the form, a failed profile request does not prevent non-SQL job editing, and a saved SQL job keeps its unavailable profile/catalog values.
- [x] **Step 2: Run** `npm.cmd --prefix frontend test -- --run apps/tenant/src/features/jobs/JobWizard.test.tsx`. Confirm the new assertions fail before implementation.
- [x] **Step 3: Add SQL-only profile loading** in `JobWizard.tsx`; handle request status without coupling it to template-catalog loading or non-SQL configuration.
- [x] **Step 4: Run** `npm.cmd --prefix frontend test -- --run apps/tenant/src/features/jobs/JobWizard.test.tsx` and confirm it passes.

### Task 3: Update SQL job help and verify the frontend

**Files:**
- Modify: `frontend/apps/tenant/src/help/content/en/sql-jobs.md`
- Modify: `frontend/apps/tenant/src/help/content/pt-BR/sql-jobs.md`

- [x] **Step 1: Update the SQL job help** to explain selecting a named profile and then choosing one of its allowed catalogs.
- [x] **Step 2: Run focused UI tests** with `npm.cmd --prefix frontend test -- --run packages/ui/src/forms/SqlTemplateForm.test.tsx apps/tenant/src/features/jobs/JobWizard.test.tsx`.
- [x] **Step 3: Build the frontend** with `npm.cmd --prefix frontend run build` and confirm it exits successfully.
