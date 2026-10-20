type Schema = Record<string, any> | boolean;

export function createSchemaDefault(schema: unknown, root: Record<string, unknown>): unknown {
  return materialize(schema, root, new Set<string>());
}

function materialize(schema: Schema | unknown, root: Record<string, unknown>, references: Set<string>): unknown {
  if (!schema || schema === true || schema === false || typeof schema !== "object") return undefined;
  const node = schema as Record<string, any>;
  if (node["x-workerDock-secretReference"] === true) return undefined;
  if (Object.hasOwn(node, "const")) return node.const;
  if (typeof node.$ref === "string") {
    if (!node.$ref.startsWith("#/$defs/")) return undefined;
    const key = node.$ref.slice(8);
    if (references.has(key)) return undefined;
    const target = (root as any).$defs?.[key];
    if (!target) return undefined;
    return materialize(target, root, new Set([...references, key]));
  }
  if (Object.hasOwn(node, "default")) return node.default;
  if (Array.isArray(node.oneOf)) {
    const branch = node.oneOf[0];
    return branch ? materialize(branch, root, references) : undefined;
  }
  const types = Array.isArray(node.type) ? node.type : [node.type];
  if (types.includes("null") && !node.properties?.[node.discriminator]) return undefined;
  if (types.includes("object") || node.properties) {
    const result: Record<string, unknown> = {};
    for (const key of node.required ?? []) {
      const child = node.properties?.[key];
      if (!child) continue;
      const value = materialize(child, root, references);
      if (value !== undefined) result[key] = value;
    }
    return result;
  }
  if (types.includes("array")) return [];
  if (Array.isArray(node.enum) && node.enum.length) return node.enum[0];
  if (types.includes("boolean")) return false;
  return undefined;
}
