import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import fixtures from "../../../../../tests/fixtures/template-schemas.json";
import { TemplateForm } from "./TemplateForm";
import { validateSchema } from "./SchemaValidation";

describe("shared schema fixtures", () => {
  it("matches every server fixture validity, issue path, and code", () => {
    for (const fixture of fixtures) {
      const issues = validateSchema(fixture.schema, fixture.configuration);
      expect(issues.length === 0, fixture.name).toBe(fixture.valid);
      if (fixture.code) expect(issues, fixture.name).toContainEqual({ path: fixture.path, code: fixture.code });
    }
  });
});

it("renders secret references without ever rendering secret value controls", async () => {
  const user = userEvent.setup();
  let value: Record<string, unknown> = {};
  const descriptor = { id: "test", version: "1", displayName: "Test", description: "Test", configurationSchema: { type: "object", properties: { credential: { type: "string", "x-workerDock-secretReference": true } } } };
  const renderForm = () => <TemplateForm descriptor={descriptor} value={value} onChange={(next) => { value = next; rerender(renderForm()); }} errors={{}} />;
  const { rerender } = render(renderForm());
  await user.type(screen.getByLabelText("Secret reference"), "logical.key");
  expect(value).toEqual({ credential: "logical.key" });
  expect(screen.queryByLabelText(/password value|token value/i)).not.toBeInTheDocument();
});
