import { describe, expect, it } from "vitest";
import { createSchemaDefault } from "./schemaDefaults";

describe("createSchemaDefault", () => {
  it("materializes only required defaults and resolves refs and the selected union", () => {
    const schema = { $defs: { flag: { type: "boolean" } }, type: "object", properties: { optional: { type: ["object", "null"], properties: { secret: { type: "string", "x-workerDock-secretReference": true } } }, choice: { oneOf: [{ properties: { kind: { const: "a" }, enabled: { $ref: "#/$defs/flag" } }, required: ["kind", "enabled"] }] } }, required: ["choice"] };
    expect(createSchemaDefault(schema, schema)).toEqual({ choice: { kind: "a", enabled: false } });
  });
});
