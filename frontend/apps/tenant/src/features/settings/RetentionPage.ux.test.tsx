import { render, screen } from "@testing-library/react";
import { beforeEach, expect, test, vi } from "vitest";
import RetentionPage from "./RetentionPage";

vi.mock("../../api/client", () => ({
  ApiError: class extends Error {},
  read: vi.fn(async () => ({ data: { logDays: 30, executionDays: 90, auditDays: 365, archiveBeforeDelete: false, archiveDays: 0 }, etag: '"1"' })),
  mutate: vi.fn(),
}));

beforeEach(() => vi.clearAllMocks());

test("retention is a settings section with grouped periods and archive controls", async () => {
  const { container } = render(<RetentionPage />);
  const section = await screen.findByRole("region", { name: "Retention and archive" });
  expect(container.querySelector("main, h1")).toBeNull();
  expect(screen.getByRole("heading", { level: 2, name: "Retention and archive" })).toBeInTheDocument();
  expect(section.querySelectorAll("section")).toHaveLength(2);
  expect(screen.getByRole("button", { name: "Save changes" })).toBeInTheDocument();
});
