# Alert Creation UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Managers can create webhook sinks and disabled alert rules in the tenant Alerts page, then explicitly enable a rule.

**Architecture:** Extend the current Alerts page and its existing PUT/GET API flow. Keep pure draft defaults and payload shaping in one local module; use the existing CSRF client and ETag editing path. Complete this plan before the tenant help-center plan, whose alert guide describes this UI.

**Tech Stack:** React 19, TypeScript 7, MUI 9, Vitest/Testing Library, Playwright, existing .NET alert API.

**Spec:** `docs/superpowers/specs/2026-10-20-tenant-help-center-and-alert-creation-design.md`

## Global Constraints

- Manage users create or edit; Read users see only incidents and deliveries and make no management API requests.
- Create uses `PUT /api/alert-sinks/{id}` or `PUT /api/alert-rules/{id}` with a new UUID and no `If-Match`; edit uses `If-Match` from a GET ETag.
- A new sink defaults enabled; a new rule is first saved disabled and requires a separate explicit enable action.
- Forms and payloads contain header names and logical secret references, never secret values.
- Preserve new IDs and drafts on failure; confirm before discarding unsaved changes.
- Add all new UI copy to both `frontend/packages/ui/locales/en.json` and `pt-BR.json`.
- Do not change backend routes or contracts. Stage and commit only this plan's files; other work exists in the shared worktree.

## Review Focus

- A sink response containing a legacy `value` field must never render or return that value (Task 1 test).
- An empty or all-disabled sink list must block rule creation with a useful message (Task 2 test).
- A lost create response followed by a retry must recover the saved UUID after 412 instead of creating a duplicate (Task 3 test).
- A slow GET for an old selection must not overwrite a newer unsaved draft (Task 3 test).
- A Read session must not request `/api/alert-rules` or `/api/alert-sinks`, even after a role/state rerender (Task 3 test).

## File structure

- Create `frontend/apps/tenant/src/features/alerts/alertDrafts.ts`: exported draft types, defaults, and secret-safe request payloads.
- Modify `frontend/apps/tenant/src/features/alerts/AlertRulesPage.tsx`: creation, activation, selection, save/recovery, and discard dialog.
- Extend `frontend/apps/tenant/src/features/alerts/AlertRulesPage.test.tsx`: behavior and API assertions.
- Create `frontend/apps/tenant/src/features/alerts/alertDrafts.test.tsx`: pure defaults and payload assertions.
- Modify `frontend/packages/ui/locales/en.json` and `pt-BR.json`: new labels and messages.
- Extend `frontend/e2e/alerts.spec.ts`: manager create/activate journey.

### Task 1: Create webhook sinks

**Interfaces:** Produce `AlertSink`, `createSinkDraft(id: string): AlertSink`, and `sinkPayload(draft: AlertSink): Omit<AlertSink, "version">` from `alertDrafts.ts`. Existing alert editing consumes the same payload function.

- [ ] **Step 1: Write failing tests.** In `alertDrafts.test.tsx`, assert defaults `{type:"webhook", enabled:true, configuration:{url:"",headers:[]}, maxAttempts:5, retryBaseDelaySeconds:10, retryMaxDelaySeconds:120, timeoutSeconds:15}` and that `sinkPayload` removes a response header's `value`. In `AlertRulesPage.test.tsx`, assert New webhook sink is manager-only, sends a UUID PUT without `If-Match`, sends only references, then GETs the same ID for its ETag; assert invalid URL/header fields keep the draft.
- [ ] **Step 2: Run the tests to see the expected failure.** From `frontend`: `npx vitest run apps/tenant/src/features/alerts/alertDrafts.test.tsx apps/tenant/src/features/alerts/AlertRulesPage.test.tsx`. Expected: FAIL for missing creation behavior.
- [ ] **Step 3: Implement.** Move the sink type and secret-stripping payload logic into `alertDrafts.ts`; add `createSinkDraft` and `sinkPayload` with the tested defaults. In `AlertRulesPage.tsx`, add New webhook sink, creation-mode state, field-level client validation, PUT without ETag, GET after save, selected-list refresh, and localized success/error copy. Preserve the draft/UUID if PUT or GET fails; existing edits still use their ETag.
- [ ] **Step 4: Verify.** Run the Step 2 command and `npm run build --workspace @workerdock/tenant` from `frontend`. Expected: all targeted tests pass and TypeScript/Vite build succeeds.
- [ ] **Step 5: Commit.** Stage only Task 1 files and commit `feat: create alert webhook sinks in tenant UI`.

### Task 2: Create disabled alert rules and enable separately

**Interfaces:** Extend `alertDrafts.ts` with `AlertRule`, `createRuleDraft(id: string, sinkId: string): AlertRule`, and `rulePayload(draft: AlertRule): Omit<AlertRule, "version">`. `AlertRulesPage` uses Task 1's sink list and API helpers.

- [ ] **Step 1: Write failing tests.** Assert defaults `{condition:"RepeatedFailure", threshold:3, windowSeconds:300, graceSeconds:0, reminderSeconds:0, enabled:false}`; a new rule with no enabled sink shows guidance and cannot save; Create rule PUT has `enabled:false` and no `If-Match`; after GET supplies ETag, a separate Enable action sends `enabled:true` with that ETag. Assert invalid condition-specific threshold and timing fields keep the draft.
- [ ] **Step 2: Run `npx vitest run apps/tenant/src/features/alerts/alertDrafts.test.tsx apps/tenant/src/features/alerts/AlertRulesPage.test.tsx` from `frontend`.** Expected: FAIL for rule creation/activation assertions.
- [ ] **Step 3: Implement.** Add `createRuleDraft` and `rulePayload`. Add New alert rule, select the first enabled sink as default, keep Enable unavailable until the disabled create and GET succeed, and use the existing edit endpoint with ETag for activation. Add localized field-level and missing-sink messages in both locale files.
- [ ] **Step 4: Re-run the Step 2 command and `npm run build --workspace @workerdock/tenant`.** Expected: targeted tests and build pass.
- [ ] **Step 5: Commit.** Stage only Task 2 files and commit `feat: create and activate alert rules in tenant UI`.

### Task 3: Preserve drafts and verify the full alert journey

**Interfaces:** In `AlertRulesPage.tsx`, use `requestSelection(kind: "rule" | "sink", id: string | undefined): void` for list/new selection and a request-generation guard for async GET responses. No new backend interface.

- [ ] **Step 1: Write failing tests.** In `AlertRulesPage.test.tsx`, assert Cancel in the discard dialog keeps the current UUID/fields; confirming switches selection; a 412 during create GETs the same UUID and shows the saved item; a failed recovery GET preserves the draft; a late old GET does not replace a newer draft; switching `canManage` to false removes controls and triggers no management fetch. In `e2e/alerts.spec.ts`, mock API responses and assert a manager can create sink, create disabled rule, then enable it, while the existing viewer test remains valid.
- [ ] **Step 2: Run the focused Vitest file and `npx playwright test e2e/alerts.spec.ts --project=chromium` from `frontend`.** Expected: new assertions fail before implementation.
- [ ] **Step 3: Implement.** Add an accessible MUI discard dialog and route all list/new selections through `requestSelection`; Cancel retains draft. Keep the same new UUID for retries, recover create 412 by GET of that UUID, and ignore GET results for superseded selections. On recovery failure, show an announced error and keep the draft. Clear manager state when permission is lost.
- [ ] **Step 4: Re-run Step 2 commands and `npm run build --workspace @workerdock/tenant`.** Expected: focused unit/browser tests and build pass.
- [ ] **Step 5: Commit.** Stage only Task 3 files and commit `fix: preserve alert drafts and recover uncertain creates`.
