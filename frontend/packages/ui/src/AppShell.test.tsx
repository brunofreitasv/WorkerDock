import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { expect, test, vi } from "vitest";
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

const navigation = [{ id: "monitoring", label: "Monitoring", items: [{ id: "monitoring", label: "Job monitoring" }, { id: "jobs", label: "Jobs" }] }];
function shell(onNavigate = vi.fn(), displayName = "A very long tenant display name that should stay inside the header") {
  return render(<AppShell t={en} displayName={displayName} navigation={navigation} active="jobs" onNavigate={onNavigate} mode="RoleProtected" authenticated onSignOut={() => {}}><div>content</div></AppShell>);
}

test("opens compact navigation and dismisses it after choosing a route", async () => {
  const onNavigate = vi.fn();
  shell(onNavigate);
  fireEvent.click(screen.getByRole("button", { name: en.openNavigation }));
  const drawer = screen.getByRole("presentation");
  fireEvent.click(within(drawer).getByRole("link", { name: "Job monitoring" }));
  expect(onNavigate).toHaveBeenCalledWith("monitoring");
  await waitFor(() => expect(within(drawer).queryByRole("link", { name: "Job monitoring" })).not.toBeInTheDocument());
});

test("keeps the long display name in a constrained header", () => {
  shell();
  expect(screen.getByText(/A very long tenant display name/)).toHaveStyle({ overflowWrap: "anywhere" });
});

test("marks the selected route in desktop navigation", () => {
  shell();
  expect(screen.getByRole("navigation", { name: en.tenant }).querySelector('[aria-current="page"]')).toHaveTextContent("Jobs");
});

test("moves keyboard focus into main content with the skip link", () => {
  shell();
  fireEvent.click(screen.getByRole("link", { name: en.skip }));
  expect(screen.getByRole("main")).toHaveFocus();
});
