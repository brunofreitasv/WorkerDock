import { render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { CursorPager } from "./CursorPager";

test("disables navigation where no previous page or next cursor exists", () => {
  render(<CursorPager previousCount={0} nextCursor={null} onPrevious={vi.fn()} onNext={vi.fn()} />);
  expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Next" })).toBeDisabled();
});

test("enables both directions when page history and a next cursor exist", () => {
  render(<CursorPager previousCount={2} nextCursor="cursor" onPrevious={vi.fn()} onNext={vi.fn()} />);
  expect(screen.getByRole("button", { name: "Previous" })).toBeEnabled();
  expect(screen.getByRole("button", { name: "Next" })).toBeEnabled();
});
