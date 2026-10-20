# Task 4 implementation report

## Result

- Added SQL template descriptor lookup for 1.0.0 and 1.1.0. The default `SqlTemplate` remains 1.0.0; its descriptor digest is pinned at `B9E119AAD34E89E0A5A4332F97A757017C2F1D1C208CF5D0FAE4E65B0902717F`.
- Only SQL parameter `value` leaves receive the dynamic-value annotation in 1.1.0.
- Added authoring checks for Procedure identifiers, one value source, unique parameter names, fixed scalar conversion, size/MAX applicability, decimal precision/scale, and explicit-zero timeout with an outer deadline. Registered both version validators.
- Kept the legacy `BuildCommandAsync` overload and conversion behavior. The versioned overload preserves ISO date calendar dates and timestamp wall-clock fields as `DateTimeKind.Unspecified`; DateTimeOffset remains offset-aware. Bound values stay in `SqlParameter` and command text is unchanged.

## Verification

- Focused command: `dotnet test tests/WorkerDock.IntegrationTests --no-restore --filter 'FullyQualifiedName~SqlTemplateAuthoringTests|FullyQualifiedName~TemplateCatalogTests|FullyQualifiedName~SqlTemplateTests.Typed_command_parameters_are_bound_without_interpolation|FullyQualifiedName~SqlTemplateTests.Timeouts_inherit_deadline_and_zero_requires_infinite_outer_policy|FullyQualifiedName~SqlTemplateTests.Installed_descriptor_is_valid_and_immutable'`
- Result: passed, 46 tests.
- Broader SQLTemplateTests filter also included SQL Server fixture cases; those require `WORKERDOCK_TEST_SQL` and were unavailable in this environment.
- The first implementation run exposed the missing-source, digest, and JSON-null metadata handling failures; fixes were followed by the passing focused run.

## Files

- `src/WorkerDock.Templates.SqlServer/SqlTemplateDescriptors.cs`
- `src/WorkerDock.Templates.SqlServer/SqlConfigurationValidator.cs`
- `src/WorkerDock.Templates.SqlServer/SqlTemplate.cs`
- `src/WorkerDock.Infrastructure/Templates/BuiltInAuthoringValidators.cs`
- `tests/WorkerDock.IntegrationTests/SqlTemplateAuthoringTests.cs`

## Review follow-up

- Guarded malformed configuration roots, non-object parameter rows, and non-string `commandKind`/`dbType` values so authoring validation returns bounded issues instead of throwing.
- Aligned `DateTime`/`DateTime2` dynamic acceptance with the declared ISO date/timestamp formats, including ReferenceDate with IsoDate.
- Matched Procedure validation to the runtime's 257-character limit.
- Added regressions for array roots, primitive parameter rows, ReferenceDate-to-DateTime2, and an overlong procedure identifier.
- RED: the three new focused regressions failed before the fixes for malformed JSON, reference-date compatibility, and length.
- GREEN: non-fixture SQL authoring/catalog/template regression command passed (49 tests). The broader filter had six SQL target fixture tests fail because `WORKERDOCK_TEST_SQL` is unset; all remaining 54 passed.
- Follow-up review: malformed/missing/non-enum `commandKind` now returns `SqlCommandKindInvalid` at `configuration.commandKind`; focused authoring tests passed (9 tests).
