# Task 2 report: typed contracts and pure calendar resolution

## Status

Completed on `codex/workerdock-final` from the designated Task 2 base (`1b50a43814fb07ea9ad604709ea8cf83dac3c282`).

## RED / GREEN evidence

- RED: the initial focused test run compiled the existing project graph and failed with missing `WorkerDock.Contracts.Values` and `DynamicValueResolver` APIs, as expected before implementation.
- During the first behavioral run, 11 of 13 cases passed; three timestamp edge cases failed because the test helper supplied non-UTC offsets to fields defined as UTC. The fixtures were corrected to represent the same instants in UTC while keeping New York as the pinned timezone.
- GREEN: `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~DynamicValueResolverTests` passed: 18 passed, 0 failed. This included month clamp and ordering, date and timestamp end boundaries, delayed scheduled date, manual override, output formats and JSON types, strict JSON, identifier stability and frozen values, culture independence, DST invalid/ambiguous rejection, unchanged ambiguous instant, unavailable manual scheduled timestamp, invalid timezone/version/offset, and calendar overflow.

## Files

- `src/WorkerDock.Contracts/Values/DynamicValuePlan.cs`
- `src/WorkerDock.Contracts/Values/ExecutionValueContext.cs`
- `src/WorkerDock.Contracts/Values/DynamicValueSnapshot.cs`
- `src/WorkerDock.Infrastructure/Values/DynamicValueResolver.cs`
- `tests/WorkerDock.IntegrationTests/DynamicValueResolverTests.cs`
- `tests/fixtures/dynamic-values.json`
- `tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj`

## Review

- Resolver reads only its arguments and uses no clock, I/O, secrets, network, or mutable global state.
- Plan/context versions, timezone IDs, UTC instants, target pointers, enum compatibility, collection and transform bounds, digest shape, and complete serialized snapshot size are validated before returning a frozen snapshot.
- Calendar operations apply months before days; date ends are inclusive and timestamp ends are exclusive next-period starts. Transformed local timestamps reject DST gaps and overlaps; untransformed offsets are formatted through the pinned zone.
- JSON plan decoding uses camelCase property names, exact enum names, rejects integer enums, unknown members, and omitted members.
- No unrelated files were changed. Focused integration tests are the only verification run for this task.

## Commit

`feat: resolve typed execution values from pinned context`

## Follow-up review fix

- Added exact-case enum parsing for all dynamic value enums and malformed `bindings` shape checks. Invalid enum spellings and `null`, object, or string binding collections now raise `DynamicBindingInvalid` without leaking `InvalidOperationException`.
- RED: the added focused regressions failed as expected: lowercase `referencedate` was accepted, and malformed collections leaked `InvalidOperationException`.
- GREEN: the focused resolver suite passed after the fix: 21 passed, 0 failed.
- Fix commit: recorded separately after the original Task 2 commit.
