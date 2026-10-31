import { render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { formatInstant } from "../../localTime";
import { PersonalSettings } from "./PersonalSettings";
import { TenantSettingsForm } from "./TenantSettingsForm";

test("personal settings separates access and preferences", () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-10-30T03:15:00Z"));
  render(<PersonalSettings t={translations.en} mode="RoleProtected" canManage={false} language="en" setLanguage={() => {}} timeZone="UTC" />);
  expect(screen.getByRole("region", { name: translations.en.access })).toBeInTheDocument();
  expect(screen.getByRole("region", { name: translations.en.preferences })).toBeInTheDocument();
  expect(screen.queryByRole("combobox", { name: translations.en.timeZone })).not.toBeInTheDocument();
  expect(screen.getByText("Time zone: UTC")).toBeInTheDocument();
  expect(screen.getByText(`Current time: ${formatInstant(new Date(), "en", "UTC")}`)).toBeInTheDocument();
  vi.useRealTimers();
});

test("tenant settings groups identity fields and save action", () => {
  render(<TenantSettingsForm t={translations.en} current={{ data: { displayName: "Dock", displayTimeZone: "UTC", version: "1" }, etag: '"1"' }} loading={false} error={false} reload={() => {}} acceptSave={() => {}} />);
  const region = screen.getByRole("region", { name: translations.en.tenantConfiguration });
  expect(region).toContainElement(screen.getByRole("textbox", { name: translations.en.displayName }));
  expect(region).toContainElement(screen.getByRole("button", { name: translations.en.save }));
  expect(screen.queryByRole("combobox", { name: translations.en.displayTimeZone })).not.toBeInTheDocument();
});
