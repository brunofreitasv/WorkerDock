import { fireEvent, render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { PeriodControl } from "./PeriodControl";
import { toUtcRange, type HistoryPeriod } from "./historyTime";

test("changes to a rolling preset immediately", () => {
  const onChange = vi.fn();
  render(<PeriodControl value={{ kind: "7d" }} timeZone="UTC" maxDays={30} language="en" onChange={onChange} />);
  fireEvent.click(screen.getByRole("button", { name: "Last 24 hours" }));
  expect(onChange).toHaveBeenCalledWith({ kind: "24h" });
});

test("shows all period labels in English", () => {
  render(<PeriodControl value={{ kind: "7d" }} timeZone="UTC" maxDays={30} language="en" onChange={vi.fn()} />);
  expect(screen.getByRole("group", { name: "History period" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Last 24 hours" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Last 7 days" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Last 30 days" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Custom" })).toBeInTheDocument();
  expect(screen.getByLabelText("Start date")).toBeInTheDocument();
  expect(screen.getByLabelText("End date")).toBeInTheDocument();
});

test("emits a valid custom range immediately and ignores an over-limit edit", () => {
  const onChange = vi.fn();
  render(<PeriodControl value={{ kind: "custom", startDate: "2026-03-08", endDate: "2026-03-09" }} timeZone="UTC" maxDays={30} language="en" onChange={onChange} />);
  fireEvent.change(screen.getByLabelText("Start date"), { target: { value: "2026-03-07" } });
  expect(onChange).toHaveBeenLastCalledWith({ kind: "custom", startDate: "2026-03-07", endDate: "2026-03-09" });
});

test("allows a custom range to be entered from a preset and emits its UTC range", () => {
  const onChange = vi.fn<(value: HistoryPeriod) => void>();
  render(<PeriodControl value={{ kind: "24h" }} timeZone="America/New_York" maxDays={30} language="en" onChange={onChange} />);

  fireEvent.click(screen.getByRole("button", { name: "Custom" }));
  const start = screen.getByLabelText("Start date");
  fireEvent.change(start, { target: { value: "2026-03-08" } });
  expect(start).toHaveValue("2026-03-08");
  expect(onChange).not.toHaveBeenCalled();

  fireEvent.change(screen.getByLabelText("End date"), { target: { value: "2026-03-09" } });
  expect(onChange).toHaveBeenLastCalledWith({ kind: "custom", startDate: "2026-03-08", endDate: "2026-03-09" });
  expect(toUtcRange(onChange.mock.lastCall![0], "America/New_York", new Date("2026-03-10T16:30:00.000Z"))).toEqual({
    fromUtc: "2026-03-08T05:00:00.000Z",
    toUtc: "2026-03-10T04:00:00.000Z",
  });
});

test("shows all period labels in Portuguese", () => {
  render(<PeriodControl value={{ kind: "7d" }} timeZone="UTC" maxDays={30} language="pt-BR" onChange={vi.fn()} />);
  expect(screen.getByRole("group", { name: "Período do histórico" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Últimas 24 horas" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Últimos 7 dias" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Últimos 30 dias" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Personalizado" })).toBeInTheDocument();
  expect(screen.getByLabelText("Data inicial")).toBeInTheDocument();
  expect(screen.getByLabelText("Data final")).toBeInTheDocument();
});
