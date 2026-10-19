import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, test, vi } from "vitest";
import RegistrationEditor, { type TenantRegistrationInput } from "./RegistrationEditor";
import TenantDirectory, { type TenantEntry } from "./TenantDirectory";

const tenant: TenantEntry = {
  id: "tenant-1",
  displayName: "Billing",
  dashboardUrl: "https://billing.example.test/",
  expectedInstanceId: "d6952d61-4075-4cb6-88b4-48189c8f85af",
  health: "Healthy",
  lastAttemptUtc: "2026-10-19T10:00:00Z",
  lastSuccessUtc: "2026-10-19T10:00:00Z",
};

test("registration submits only safe link metadata and a vault reference, and requires HTTPS", async () => {
  const user = userEvent.setup();
  const onSave = vi.fn(async (_input: TenantRegistrationInput) => {});
  render(<RegistrationEditor open onClose={() => {}} onSave={onSave} />);

  await user.type(screen.getByRole("textbox", { name: "Tenant name" }), "Billing");
  await user.type(screen.getByRole("textbox", { name: "Expected instance ID" }), tenant.expectedInstanceId);
  await user.type(screen.getByRole("textbox", { name: "Dashboard URL" }), "http://billing.example.test");
  await user.type(screen.getByRole("textbox", { name: "Health credential reference" }), "TENANT_HEALTH_TOKEN");
  await user.click(screen.getByRole("button", { name: "Save tenant" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Use an HTTPS dashboard URL.");
  expect(onSave).not.toHaveBeenCalled();

  await user.clear(screen.getByRole("textbox", { name: "Dashboard URL" }));
  await user.type(screen.getByRole("textbox", { name: "Dashboard URL" }), tenant.dashboardUrl);
  await user.click(screen.getByRole("button", { name: "Save tenant" }));
  expect(onSave).toHaveBeenCalledWith({
    displayName: "Billing",
    expectedInstanceId: tenant.expectedInstanceId,
    dashboardUrl: tenant.dashboardUrl,
    healthCredentialReference: "TENANT_HEALTH_TOKEN",
  });
  expect(Object.keys(onSave.mock.calls[0]![0])).toEqual([
    "displayName",
    "expectedInstanceId",
    "dashboardUrl",
    "healthCredentialReference",
  ]);
}, 15000);

test("viewer can follow tenant links and refresh health without management controls", () => {
  render(
    <TenantDirectory
      tenants={[tenant]}
      canManage={false}
      loading={false}
      onRefresh={() => {}}
      onSearch={() => {}}
      onLoadMore={() => {}}
      onAdd={() => {}}
      onEdit={() => {}}
      onRemove={() => {}}
    />,
  );
  expect(screen.getByRole("link", { name: /Billing/ })).toHaveAttribute(
    "href",
    tenant.dashboardUrl,
  );
  expect(screen.getByText("Healthy")).toBeVisible();
  expect(screen.getByRole("button", { name: "Refresh health for Billing" })).toBeEnabled();
  expect(screen.queryByRole("button", { name: "Add tenant" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();
});
