import { fireEvent, render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { HistoryFilters, HistoryResults } from "./HistoryLayout";

test("places screen filters and clear action in a labeled surface", () => {
  const onClear = vi.fn();
  render(<HistoryFilters title="Filters" onClear={onClear} clearLabel="Clear filters"><input aria-label="State" /></HistoryFilters>);
  const section = screen.getByRole("region", { name: "Filters" });
  expect(section).toContainElement(screen.getByRole("textbox", { name: "State" }));
  fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
  expect(onClear).toHaveBeenCalledOnce();
});

test("uses a Portuguese default clear label when the page omits clearLabel", () => {
  const onClear = vi.fn();
  render(<HistoryFilters title="Filtros" language="pt-BR" onClear={onClear}><input aria-label="Estado" /></HistoryFilters>);
  fireEvent.click(screen.getByRole("button", { name: "Limpar filtros" }));
  expect(onClear).toHaveBeenCalledOnce();
});

test("places refresh, updated time, rows and pagination in a separate results surface", () => {
  const onRefresh = vi.fn();
  render(<HistoryResults title="Results" updatedAt="Updated: 12:00" refreshLabel="Refresh" onRefresh={onRefresh} footer={<span>Page 1</span>}><span>Row 1</span></HistoryResults>);
  const section = screen.getByRole("region", { name: "Results" });
  const header = section.firstElementChild;
  expect(header).toContainElement(screen.getByText("Updated: 12:00"));
  expect(header).toContainElement(screen.getByRole("button", { name: "Refresh" }));
  expect(header).not.toContainElement(screen.getByText("Row 1"));
  expect(section).toContainElement(screen.getByText("Row 1"));
  expect(section).toContainElement(screen.getByText("Page 1"));
  fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
  expect(onRefresh).toHaveBeenCalledOnce();
});
