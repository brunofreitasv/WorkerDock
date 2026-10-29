import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { SqlTemplateForm } from "./SqlTemplateForm";
import type { TemplateFormProps } from "./TemplateForm";

const descriptor = { id: "workerdock.sql-server", version: "1.1.0", displayName: "SQL Server", description: "SQL", configurationSchema: {} };
const profiles = { status: "ready" as const, items: [
  { current: { profileId: "p1", name: "Warehouse", server: "db-one", allowedCatalogs: ["Ops", "Audit"] }, retired: false },
  { current: { profileId: "p2", name: "Reports", server: "db-two", allowedCatalogs: ["Analytics"] }, retired: false },
] };
function renderForm(value: Record<string, unknown>, sqlProfiles: TemplateFormProps["sqlProfiles"], onChange = () => {}) {
  return render(<SqlTemplateForm descriptor={descriptor} value={value} onChange={onChange} errors={{}} sqlProfiles={sqlProfiles} />);
}

describe("SQL profile and catalog selectors", () => {
  it("shows active profile names with servers and only the selected profile catalogs", async () => {
    renderForm({ profileId: "p1", catalog: "Ops" }, profiles);
    const profile = screen.getByRole("combobox", { name: "Connection profile" });
    expect(profile).toHaveTextContent("Warehouse (db-one)");
    const catalog = screen.getByRole("combobox", { name: "Catalog" });
    await userEvent.setup().click(catalog);
    expect(screen.getByRole("option", { name: "Ops" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Audit" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Analytics" })).not.toBeInTheDocument();
  });

  it("stores profile ID and clears catalog when the profile changes", async () => {
    let value: Record<string, unknown> = { profileId: "p1", catalog: "Ops" };
    const onChange = (next: Record<string, unknown>) => { value = next; };
    const { rerender } = renderForm(value, profiles, onChange);
    const user = userEvent.setup();
    await user.click(screen.getByRole("combobox", { name: "Connection profile" }));
    await user.click(screen.getByRole("option", { name: "Reports (db-two)" }));
    expect(value).toMatchObject({ profileId: "p2", catalog: "" });
    rerender(<SqlTemplateForm descriptor={descriptor} value={value} onChange={onChange} errors={{}} sqlProfiles={profiles} />);
    await user.click(screen.getByRole("combobox", { name: "Catalog" }));
    expect(screen.getByRole("option", { name: "Analytics" })).toBeInTheDocument();
  });

  it("shows loading, error, and empty profile list states", () => {
    const base = { profileId: "", catalog: "", commandTimeoutSeconds: 10 };
    const { rerender } = renderForm(base, { status: "loading", items: [] });
    expect(screen.getByText("Loading connection profiles…")).toBeInTheDocument();
    rerender(<SqlTemplateForm descriptor={descriptor} value={base} onChange={() => {}} errors={{}} sqlProfiles={{ status: "error", items: [] }} />);
    expect(screen.getByText("Unable to load connection profiles.")).toBeInTheDocument();
    rerender(<SqlTemplateForm descriptor={descriptor} value={base} onChange={() => {}} errors={{}} sqlProfiles={{ status: "ready", items: [] }} />);
    expect(screen.getByText("No active connection profiles are available.")).toBeInTheDocument();
  });

  it("preserves a saved unavailable profile and catalog visibly", () => {
    renderForm({ profileId: "old-id", catalog: "OldCatalog" }, profiles);
    const profile = screen.getByRole("combobox", { name: "Connection profile" });
    expect(profile).toHaveTextContent("Unavailable profile (old-id)");
    const catalog = screen.getByRole("combobox", { name: "Catalog" });
    expect(catalog).toHaveTextContent("Unavailable catalog (OldCatalog)");
  });
});
