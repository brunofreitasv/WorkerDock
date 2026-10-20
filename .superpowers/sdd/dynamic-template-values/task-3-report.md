# Task 3 implementation report

## Evidence

- RED: focused test run failed at compile time because `DynamicValueValidation`, `DynamicValueTargets.Apply`, and `TemplateSchemaValidator.ValidateAuthoring` did not exist.
- GREEN: `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~DynamicValueValidationTests|FullyQualifiedName~TemplateCatalogTests'` passed (47 tests).
- Covered safe unknown-target errors, missing bound leaves with required siblings, escaped map keys and `$ref`, selected `oneOf`, malformed/noncanonical/out-of-range indices, overlaps, binding limit, sensitive ancestors, output type mismatch, and scalar replacement.

## Files

- Added `DynamicValueTargets`, `DynamicValueValidation`, authoring validator/service contracts and empty built-in service composition.
- Extended `TemplateSchemaValidator` for the dynamic scalar annotation and authoring required-field skip behavior.
- Added `DynamicValueValidationTests` and one schema fixture.

## Self-review

- Target errors use a binding index and safe code; unknown target strings are never returned.
- Only declared scalar leaves can be supplied. Missing parents and array elements cannot be created; eligible missing scalar leaves are skipped while required siblings continue to validate.
- Binding plan freeze enforces count, pointer, bounds, overlap, and source/format constraints. Combined configuration and plan payload is capped at 49,152 bytes; returned issues are capped at 64.
- Authoring validation does not resolve values, consult secrets, or call external services. Custom validators are explicit registrations.
- `DynamicValueTargets.Apply` accepts validated scalar values only and returns a detached JSON document.

## Commit

`feat: validate declared dynamic targets and authoring inputs` (committed on `codex/workerdock-final`; final commit ID is in the task handoff).

## Review follow-up

- Reproduced nullable scalar target failure with `type:["string","null"]`: the focused suite returned `DynamicTargetInvalid` for a compatible string binding.
- Updated eligibility to accept the scalar type-array subset (including nullable scalar schemas) consistently with descriptor and output compatibility checks.
- Regression and focused verification: `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~DynamicValueValidationTests|FullyQualifiedName~TemplateCatalogTests'` passed (48 tests).
