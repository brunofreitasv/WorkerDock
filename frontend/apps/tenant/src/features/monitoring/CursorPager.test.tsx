import { render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { CursorPager } from "./CursorPager";

test("disables navigation where no previous page or next cursor exists", () => {
  render(<CursorPager previousCount={0} nextCursor={null} language="en" onPrevious={vi.fn()} onNext={vi.fn()} />);
  expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Next" })).toBeDisabled();
});

test("enables both directions when page history and a next cursor exist", () => {
  render(<CursorPager previousCount={2} nextCursor="cursor" language="en" onPrevious={vi.fn()} onNext={vi.fn()} />);
  expect(screen.getByRole("button", { name: "Previous" })).toBeEnabled();
  expect(screen.getByRole("button", { name: "Next" })).toBeEnabled();
});

test("shows pager labels in English", () => {
  render(<CursorPager previousCount={0} nextCursor="cursor" language="en" onPrevious={vi.fn()} onNext={vi.fn()} />);
  expect(screen.getByRole("navigation", { name: "History pages" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
  expect(screen.getByText("Page 1")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Next" })).toBeEnabled();
});

test("shows pager labels in Portuguese", () => {
  render(<CursorPager previousCount={0} nextCursor="cursor" language="pt-BR" onPrevious={vi.fn()} onNext={vi.fn()} />);
  expect(screen.getByRole("navigation", { name: "Páginas do histórico" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Anterior" })).toBeDisabled();
  expect(screen.getByText("Página 1")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Próxima" })).toBeEnabled();
});
