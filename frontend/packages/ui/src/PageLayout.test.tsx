import { render, screen } from "@testing-library/react";
import { expect, test } from "vitest";
import { EmptyState, FilterPanel, PageHeader, ResultsPanel, SectionSurface, StatusBadge } from "./PageLayout";

test("renders a page heading and its action", () => {
  render(<PageHeader title="Executions" description="Recent activity" actions={<button>Refresh</button>} />);
  expect(screen.getByRole("heading", { level: 1, name: "Executions" })).toBeVisible();
  expect(screen.getByText("Recent activity")).toBeVisible();
  expect(screen.getByRole("button", { name: "Refresh" })).toBeVisible();
});

test("labels filter and result surfaces while keeping their actions", () => {
  render(<><FilterPanel title="Filters" actions={<button>Clear</button>}><input aria-label="Search" /></FilterPanel><ResultsPanel title="Results" meta="12 items" actions={<button>Refresh</button>} footer={<span>Page 1</span>}><span>Rows</span></ResultsPanel></>);
  expect(screen.getByRole("region", { name: "Filters" })).toContainElement(screen.getByRole("button", { name: "Clear" }));
  expect(screen.getByRole("region", { name: "Results" })).toHaveTextContent("12 items");
  expect(screen.getByRole("region", { name: "Results" })).toHaveTextContent("Page 1");
});

test("renders section and empty headings with text status", () => {
  render(<><SectionSurface title="Activity" description="Today"><EmptyState title="No results" description="Try another filter" /></SectionSurface><StatusBadge label="Open" tone="warning" /></>);
  expect(screen.getByRole("heading", { name: "Activity" })).toBeVisible();
  expect(screen.getByRole("heading", { name: "No results" })).toBeVisible();
  expect(screen.getByText("Open")).toBeVisible();
});
