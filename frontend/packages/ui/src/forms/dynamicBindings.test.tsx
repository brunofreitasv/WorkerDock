import { expect, it } from "vitest";
import { removeArrayItem, renameMapEntry } from "./dynamicBindings";

const plan = { resolverVersion: 1 as const, timeZoneId: "UTC", bindings: [
  { target: "/parameters/0/value", source: "ReferenceDate" as const, timeZoneId: null, months: 0, days: 0, boundary: "None" as const, format: "IsoDate" as const },
  { target: "/parameters/1/value", source: "ReferenceDate" as const, timeZoneId: null, months: 0, days: 0, boundary: "None" as const, format: "IsoDate" as const },
] };

it("moves bindings with remaining array rows and removes the deleted row binding", () => {
  expect(removeArrayItem(plan, "/parameters", 0)?.bindings.map(binding => binding.target)).toEqual(["/parameters/0/value"]);
  expect(removeArrayItem(plan, "/parameters", 1)?.bindings.map(binding => binding.target)).toEqual(["/parameters/0/value"]);
});

it("renames escaped map keys by pointer segment and rejects prototype keys", () => {
  const mapPlan = { ...plan, bindings: [{ ...plan.bindings[0], target: "/parameters/a~1b~0c" }] };
  expect(renameMapEntry(mapPlan, "/parameters", "a/b~c", "next/key")?.bindings[0].target).toBe("/parameters/next~1key");
  expect(renameMapEntry(mapPlan, "/parameters", "a/b~c", "__proto__")?.bindings[0].target).toBe("/parameters/a~1b~0c");
});
