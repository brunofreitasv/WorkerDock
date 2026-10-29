import { fireEvent, render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { PeriodControl } from "./PeriodControl";

test("changes to a rolling preset immediately", () => {
  const onChange = vi.fn();
  render(<PeriodControl value={{ kind: "7d" }} timeZone="UTC" maxDays={30} onChange={onChange} />);
  fireEvent.click(screen.getByRole("button", { name: "Last 24 hours" }));
  expect(onChange).toHaveBeenCalledWith({ kind: "24h" });
});

test("emits a valid custom range immediately and ignores an over-limit edit", () => {
  const onChange = vi.fn();
  render(<PeriodControl value={{ kind: "custom", startDate: "2026-03-08", endDate: "2026-03-09" }} timeZone="UTC" maxDays={30} onChange={onChange} />);
  fireEvent.change(screen.getByLabelText("Start date"), { target: { value: "2026-03-07" } });
  expect(onChange).toHaveBeenLastCalledWith({ kind: "custom", startDate: "2026-03-07", endDate: "2026-03-09" });
});
