import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import fixtures from "../../../../../tests/fixtures/template-schemas.json";
import { TemplateForm } from "./TemplateForm";
import { validateSchema } from "./SchemaValidation";
import { createSchemaDefault } from "./schemaDefaults";
import { translations } from "../preferences";
import type { DynamicValuePlan } from "./dynamicValueTypes";

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

it("shows server validation paths on their matching schema fields", () => {
  const descriptor = { id: "test", version: "1", displayName: "Test", description: "Test", configurationSchema: { type: "object", properties: { count: { type: "integer", minimum: 1 } } } };
  render(<TemplateForm descriptor={descriptor} value={{ count: 0 }} onChange={() => {}} errors={{ "configuration.count": "Minimum" }} />);
  expect(screen.getByLabelText("Count")).toHaveAttribute("aria-invalid", "true");
  expect(screen.getByText("Minimum")).toBeInTheDocument();
});

it("new_parameter_row_submits_visible_boolean_defaults", async () => {
  const user = userEvent.setup();
  let value: Record<string, unknown> = { parameters: [] };
  const descriptor = { id: "test", version: "1", displayName: "Test", description: "Test", configurationSchema: { type: "object", properties: { parameters: { type: "array", items: { type: "object", properties: { useExecutionKey: { type: "boolean" }, isNull: { type: "boolean" }, secret: { type: "string", "x-workerDock-secretReference": true } }, required: ["useExecutionKey", "isNull"] } } } } };
  const { rerender } = render(<TemplateForm descriptor={descriptor} value={value} onChange={next => { value = next; rerender(<TemplateForm descriptor={descriptor} value={value} onChange={n => { value = n; }} errors={{}} />); }} errors={{}} />);
  await user.click(screen.getByRole("button", { name: "Add item" }));
  const row = (value.parameters as Record<string, unknown>[])[0];
  expect(row).toMatchObject({ useExecutionKey: false, isNull: false });
  expect(row).not.toHaveProperty("secret");
});

it("does not add empty secret-reference array or map values", () => {
  let value: Record<string, unknown> = { headers: {}, tokens: [] };
  const descriptor = { id: "test", version: "1", displayName: "Test", description: "Test", configurationSchema: { $defs: { secret: { type: "string", "x-workerDock-secretReference": true } }, type: "object", properties: { headers: { type: "object", additionalProperties: { $ref: "#/$defs/secret" } }, tokens: { type: "array", items: { $ref: "#/$defs/secret" } } } } };
  const { rerender } = render(<TemplateForm descriptor={descriptor} value={value} onChange={next => { value = next; rerender(<TemplateForm descriptor={descriptor} value={value} onChange={n => { value = n; }} errors={{}} />); }} errors={{}} />);
  expect(screen.getByRole("button", { name: "Add entry" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Add item" })).toBeDisabled();
  expect(value).toEqual({ headers: {}, tokens: [] });
});

it("creates defaults for refs and selected oneOf branches without nullable optional objects", () => {
  const schema = { $defs: { flag: { type: "boolean" } }, type: "object", properties: { optional: { type: ["object", "null"], properties: { token: { type: "string", "x-workerDock-secretReference": true } } }, choice: { oneOf: [{ properties: { kind: { const: "a" }, enabled: { $ref: "#/$defs/flag" } }, required: ["kind", "enabled"] }, { properties: { kind: { const: "b" }, count: { type: "integer" } }, required: ["kind", "count"] }] } }, required: ["choice"] };
  expect(createSchemaDefault(schema, schema)).toEqual({ choice: { kind: "a", enabled: false } });
});

it("maps missing required array members to their concrete row path", () => {
  const schema = { type: "object", properties: { parameters: { type: "array", items: { type: "object", properties: { isNull: { type: "boolean" } }, required: ["isNull"] } } } };
  expect(validateSchema(schema, { parameters: [{ isNull: true }, {}] })).toContainEqual({ path: "configuration.parameters[1].isNull", code: "Required" });
});

it("treats a bound eligible scalar as supplied while preserving other schema errors", () => {
  const schema = { type: "object", required: ["parameters"], properties: { parameters: { type: "object", required: ["referenceDate", "name"], properties: { referenceDate: { type: "string", "x-workerDock-dynamicValue": true }, name: { type: "string" } } } } };
  expect(validateSchema(schema, { parameters: {} }, new Set(["/parameters/referenceDate"]))).toEqual([{ path: "configuration.parameters.name", code: "Required" }]);
  expect(validateSchema({ type: "object", required: ["name"], properties: { name: { type: "string" } } }, {}, new Set(["/name"]))).toEqual([{ path: "configuration.name", code: "Required" }]);
});

it("uses explicit SQL procedure/query language and hides decimal metadata for Date parameters", () => {
  const descriptor = { id: "workerdock.sql-server", version: "1.1.0", displayName: "SQL Server", description: "SQL template", configurationSchema: {} };
  const plan: DynamicValuePlan = { resolverVersion: 1, timeZoneId: "UTC", bindings: [] };
  render(<TemplateForm descriptor={descriptor} value={{ profileId: "profile", catalog: "jobs", commandKind: "Procedure", commandText: "dbo.Job", parameters: [{ name: "@Dt_Ref", dbType: "Date", value: "2026-10-20", useExecutionKey: false, isNull: false }], useTransaction: true, commandTimeoutSeconds: null }} onChange={() => {}} errors={{}} dynamicValues={plan} onDynamicValuesChange={() => {}} t={translations.en} />);
  expect(screen.getByLabelText("Stored procedure name")).toHaveValue("dbo.Job");
  expect(screen.getByText(/Enter only the procedure name/)).toBeInTheDocument();
  expect(screen.getByRole("combobox", { name: "Value source" })).toHaveTextContent("Fixed value");
  expect(screen.queryByLabelText("Decimal places (scale)")).not.toBeInTheDocument();
  expect(screen.queryByLabelText(/Use Execution Key|Is Null/i)).not.toBeInTheDocument();
  expect(screen.getByText(/No SQL command timeout is set/)).toBeInTheDocument();
});

it("explains query text and parameters separately", () => {
  const descriptor = { id: "workerdock.sql-server", version: "1.0.0", displayName: "SQL Server", description: "SQL template", configurationSchema: {} };
  render(<TemplateForm descriptor={descriptor} value={{ commandKind: "Query", commandText: "SELECT * FROM dbo.Items WHERE Id = @Id", parameters: [], useTransaction: true, commandTimeoutSeconds: 30 }} onChange={() => {}} errors={{}} t={translations.en} />);
  expect(screen.getByLabelText("SQL statement")).toHaveValue("SELECT * FROM dbo.Items WHERE Id = @Id");
  expect(screen.getByText(/complete SQL statement using @parameter placeholders/)).toBeInTheDocument();
  expect(screen.getByText(/Add matching parameter names, types, and values below/)).toBeInTheDocument();
  expect(screen.getByText(/does not support dynamic values/)).toBeInTheDocument();
});

it("keeps a second row error attached while the first row changes", async () => {
  const user = userEvent.setup();
  let value: Record<string, unknown> = { parameters: [{ name: "first" }, {}] };
  const descriptor = { id: "test", version: "1", displayName: "Test", description: "Test", configurationSchema: { type: "object", properties: { parameters: { type: "array", items: { type: "object", properties: { name: { type: "string" } } } } } } };
  const { rerender } = render(<TemplateForm descriptor={descriptor} value={value} onChange={next => { value = next; rerender(<TemplateForm descriptor={descriptor} value={value} onChange={n => { value = n; }} errors={{ "configuration.parameters[1].name": "Required" }} />); }} errors={{ "configuration.parameters[1].name": "Required" }} />);
  const names = screen.getAllByLabelText("Name");
  expect(names[1]).toHaveAttribute("aria-invalid", "true");
  await user.clear(names[0]);
  await user.type(names[0], "updated");
  expect(screen.getAllByLabelText("Name")[1]).toHaveAttribute("aria-invalid", "true");
  expect(screen.getByText("Required")).toBeInTheDocument();
});

it("shows root configuration errors when a required form value is missing", () => {
  const descriptor = { id: "test", version: "1", displayName: "Test", description: "Test", configurationSchema: { type: "object", properties: { name: { type: "string" } }, required: ["name"] } };
  render(<TemplateForm descriptor={descriptor} value={{}} onChange={() => {}} errors={{ configuration: "Required" }} />);
  expect(screen.getAllByRole("alert")).toHaveLength(1);
  expect(screen.getByRole("alert")).toHaveTextContent("Required");
});

it("shows a root oneOf validation error only once", () => {
  const descriptor = { id: "test", version: "1", displayName: "Test", description: "Test", configurationSchema: { type: "object", oneOf: [
    { properties: { kind: { const: "a" }, first: { type: "string" } }, required: ["kind"] },
    { properties: { kind: { const: "b" }, second: { type: "string" } }, required: ["kind"] },
  ] } };
  render(<TemplateForm descriptor={descriptor} value={{ kind: "a" }} onChange={() => {}} errors={{ configuration: "OneOf" }} />);
  expect(screen.getAllByRole("alert")).toHaveLength(1);
  expect(screen.getByRole("alert")).toHaveTextContent("OneOf");
});
