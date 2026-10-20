import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { RunNowDialog } from "./RunNowDialog";

test("manual_override_confirmation_posts_date_only", async () => {
  const onConfirm = vi.fn(async (_date: string | null) => {});
  const user = userEvent.setup();
  render(<RunNowDialog open timeZoneId="America/Sao_Paulo" supportsReferenceDate busy={false} onClose={vi.fn()} onConfirm={onConfirm} t={translations.en} />);
  expect(screen.getByText(translations.en.runNowReferenceDatePrompt.replace("{timeZoneId}", "America/Sao_Paulo"))).toBeInTheDocument();
  await user.type(screen.getByLabelText(translations.en.dynamicReferenceDate), "2026-10-19");
  await user.click(screen.getByRole("button", { name: translations.en.runNow }));
  expect(onConfirm).toHaveBeenCalledWith("2026-10-19");
});

test("unsupported template does not submit a reference date override", async () => {
  const onConfirm = vi.fn(async (_date: string | null) => {});
  const user = userEvent.setup();
  render(<RunNowDialog open timeZoneId="UTC" supportsReferenceDate={false} busy={false} onClose={vi.fn()} onConfirm={onConfirm} t={translations.en} />);
  expect(screen.getByRole("status")).toHaveTextContent(translations.en.runNowReferenceDateUnavailable);
  await user.click(screen.getByRole("button", { name: translations.en.runNow }));
  expect(onConfirm).toHaveBeenCalledWith(null);
});
