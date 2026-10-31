import { expect, test } from "vitest";
import { formatInstant } from "./localTime";

test("formats offset-bearing instants in the client zone", () => {
  expect(formatInstant("2026-10-30T03:15:00Z", "en", "America/Sao_Paulo")).toContain("10/30/2026");
  expect(formatInstant("2026-10-30T03:15:00Z", "en", "America/Sao_Paulo")).toContain("12:15:00 AM");
});

test.each([null, "bad", "2026-10-30T03:15:00"]) ("renders invalid instant %s as a placeholder", value => {
  expect(formatInstant(value, "en", "America/Sao_Paulo")).toBe("—");
});
