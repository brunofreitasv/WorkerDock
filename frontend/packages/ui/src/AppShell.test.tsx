import { render, screen } from "@testing-library/react";
import { expect, test } from "vitest";
import { AppShell } from "./AppShell";
import en from "../locales/en.json";

test("renders labelled navigation groups and a tenant display name", () => {
  render(<AppShell
    t={en}
    displayName="Acme North"
    navigation={[
      { id: "monitoring", label: "Monitoring", items: [{ id: "monitoring", label: "Job monitoring" }] },
      { id: "maintenance", label: "Maintenance", items: [{ id: "jobs", label: "Jobs" }] },
      { id: "administration", label: "Administration", items: [{ id: "settings", label: "Settings" }] },
    ]}
    active="monitoring"
    onNavigate={() => {}}
    mode="RoleProtected"
    authenticated
    onSignOut={() => {}}
  ><div>content</div></AppShell>);

  expect(screen.getByText("Acme North")).toBeVisible();
  expect(screen.getByRole("navigation", { name: en.tenant })).toBeVisible();
  const groupHeadings = screen.getAllByRole("heading", { level: 2 }).map((heading) => heading.textContent);
  expect(groupHeadings).toEqual(["Monitoring", "Maintenance", "Administration"]);
  const monitoring = screen.getByRole("region", { name: "Monitoring" });
  expect(monitoring).toContainElement(screen.getByRole("link", { name: "Job monitoring" }));
  expect(screen.getByRole("link", { name: "Job monitoring" })).toHaveAttribute("aria-current", "page");
});
