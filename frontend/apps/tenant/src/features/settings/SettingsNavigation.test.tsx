import { fireEvent, render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { SettingsNavigation } from "./SettingsNavigation";

test("settings tabs expose selection and keyboard navigation", () => {
  const select = vi.fn();
  render(<SettingsNavigation t={translations.en} selected="personal" canManage onSelect={select} />);
  const personal = screen.getByRole("tab", { name: translations.en.personalSettings });
  const general = screen.getByRole("tab", { name: translations.en.tenantConfiguration });
  expect(personal).toHaveAttribute("aria-selected", "true");
  expect(general).toHaveAttribute("aria-selected", "false");
  personal.focus();
  fireEvent.keyDown(personal, { key: "ArrowRight" });
  expect(select).toHaveBeenCalledWith("general");
});

test("read-only users see only personal settings", () => {
  render(<SettingsNavigation t={translations.en} selected="personal" canManage={false} onSelect={() => {}} />);
  expect(screen.getAllByRole("tab")).toHaveLength(1);
  expect(screen.queryByRole("tab", { name: translations.en.tenantConfiguration })).not.toBeInTheDocument();
});
