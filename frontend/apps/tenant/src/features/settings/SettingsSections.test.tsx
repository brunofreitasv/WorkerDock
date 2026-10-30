import { render, screen } from "@testing-library/react";
import { expect, test } from "vitest";
import { translations } from "@workerdock/ui";
import { PersonalSettings } from "./PersonalSettings";
import { TenantSettingsForm } from "./TenantSettingsForm";

test("personal settings separates access and preferences", () => {
  render(<PersonalSettings t={translations.en} mode="RoleProtected" canManage={false} language="en" setLanguage={() => {}} timeZone="UTC" setTimeZone={() => {}} />);
  expect(screen.getByRole("region", { name: translations.en.access })).toBeInTheDocument();
  expect(screen.getByRole("region", { name: translations.en.preferences })).toBeInTheDocument();
});

test("tenant settings groups identity fields and save action", () => {
  render(<TenantSettingsForm t={translations.en} current={{ data: { displayName: "Dock", displayTimeZone: "UTC", version: "1" }, etag: '"1"' }} loading={false} error={false} reload={() => {}} acceptSave={() => {}} />);
  const region = screen.getByRole("region", { name: translations.en.tenantConfiguration });
  expect(region).toContainElement(screen.getByRole("textbox", { name: translations.en.displayName }));
  expect(region).toContainElement(screen.getByRole("button", { name: translations.en.save }));
});
