export type ValidationIssue = { path: string; code: string };
type Schema = Record<string, any> | boolean;

export function validateSchema(schema: Schema, value: unknown, suppliedPointers: ReadonlySet<string> = new Set()): ValidationIssue[] {
  return validateSchemaAtDepth(schema, value, suppliedPointers, 0);
}

function validateSchemaAtDepth(schema: Schema, value: unknown, suppliedPointers: ReadonlySet<string>, initialDepth: number): ValidationIssue[] {
  const issues: ValidationIssue[] = [];
  const root: any = schema;
  const visit = (node: any, current: any, path: string, depth: number, pointer: string): void => {
    if (issues.length >= 64 || depth > 32) { add(path, depth > 32 ? "ConfigurationTooDeep" : "ValidationBudgetExceeded"); return; }
    if (node === true) return;
    if (node === false) { add(path, "SchemaRejected"); return; }
    if (!node || typeof node !== "object") { add(path, "InvalidSchema"); return; }
    if (suppliedPointers.has(pointer) && isDynamicEligible(node)) return;
    if (typeof node.$ref === "string") {
      const key = node.$ref.startsWith("#/$defs/") ? node.$ref.slice(8) : "";
      if (!key || !root.$defs?.[key]) { add(path, "RemoteSchemaReference"); return; }
      visit(root.$defs[key], current, path, depth + 1, pointer);
    }
    if (node.type && !(Array.isArray(node.type) ? node.type.some((t: string) => matches(t, current)) : matches(node.type, current))) { add(path, "Type"); return; }
    if (node["x-workerDock-secretReference"] === true && (typeof current !== "string" || !/^[A-Za-z0-9_.-]{1,128}$/.test(current))) add(path, "SecretReferenceInvalid");
    if (node.enum && !node.enum.some((item: unknown) => deepEqual(item, current))) add(path, "Enum");
    if (Object.hasOwn(node, "const") && !deepEqual(node.const, current)) add(path, "Const");
    if (node.oneOf) {
      const matchesCount = node.oneOf.filter((branch: Schema) => validateBranch(branch, current, depth + 1, pointer)).length;
      if (matchesCount !== 1) add(path, "OneOf");
    }
    if (current && typeof current === "object" && !Array.isArray(current)) {
      if (node.maxProperties !== undefined && Object.keys(current).length > node.maxProperties) add(path, "MaxProperties");
      for (const required of node.required ?? []) {
        const childSchema = node.properties?.[required];
        const target = `${pointer}/${escapePointerSegment(required)}`;
        if (!Object.hasOwn(current, required) && !(suppliedPointers.has(target) && isDynamicEligible(childSchema))) add(childSchema ? `${path}.${required}` : path, "Required");
      }
      for (const [key, child] of Object.entries(current)) {
        if (node.properties && Object.hasOwn(node.properties, key)) visit(node.properties[key], child, `${path}.${key}`, depth + 1, `${pointer}/${escapePointerSegment(key)}`);
        else if (Object.hasOwn(node, "additionalProperties")) {
          if (node.additionalProperties === false) add(path, "AdditionalProperty");
          else visit(node.additionalProperties, child, `${path}.additional`, depth + 1, `${pointer}/${escapePointerSegment(key)}`);
        }
      }
    } else if (Array.isArray(current)) {
      bound(current.length, node.minItems, "MinItems", false, path);
      bound(current.length, node.maxItems, "MaxItems", true, path);
      if (node.uniqueItems && current.some((item, i) => current.slice(0, i).some((other) => deepEqual(item, other)))) add(path, "UniqueItems");
      if (node.items) current.forEach((item, i) => visit(node.items, item, `${path}[${i}]`, depth + 1, `${pointer}/${i}`));
    } else if (typeof current === "string") {
      const length = [...current].length;
      bound(length, node.minLength, "MinLength", false, path);
      bound(length, node.maxLength, "MaxLength", true, path);
      if (node.pattern && !new RegExp(node.pattern).test(current)) add(path, "Pattern");
      if (node.format === "uuid" && !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(current)) add(path, "Format");
      if (node.format === "date-time" && (!/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)$/.test(current) || Number.isNaN(Date.parse(current)))) add(path, "Format");
    } else if (typeof current === "number") {
      bound(current, node.minimum, "Minimum", false, path);
      bound(current, node.maximum, "Maximum", true, path);
      if (node.exclusiveMinimum !== undefined && current <= node.exclusiveMinimum) add(path, "ExclusiveMinimum");
      if (node.exclusiveMaximum !== undefined && current >= node.exclusiveMaximum) add(path, "ExclusiveMaximum");
      if (node.multipleOf !== undefined && !multipleOf(current, node.multipleOf)) add(path, "MultipleOf");
    }
  };
  const validateBranch = (branch: Schema, candidate: unknown, depth: number, pointer: string) => {
    const nested = validateSchemaInternal(branch, candidate, depth, pointer);
    return nested.length === 0;
  };
  const add = (path: string, code: string) => { if (issues.length < 64) issues.push({ path, code }); };
  const bound = (actual: number, limit: number | undefined, code: string, upper: boolean, path: string) => {
    if (limit !== undefined && (upper ? actual > limit : actual < limit)) add(path, code);
  };
  visit(schema, value, "configuration", initialDepth, "");
  return issues;

  function validateSchemaInternal(branch: Schema, candidate: unknown, depth: number, pointer: string): ValidationIssue[] {
    const branchPointers = new Set([...suppliedPointers].filter(target => target === pointer || target.startsWith(pointer + "/")).map(target => target.slice(pointer.length)));
    return validateSchemaAtDepth(branch, candidate, branchPointers, depth);
  }
}

function escapePointerSegment(value: string): string { return value.replaceAll("~", "~0").replaceAll("/", "~1"); }
function isDynamicEligible(schema: any): boolean {
  if (!schema || schema["x-workerDock-dynamicValue"] !== true || schema["x-workerDock-sensitive"] === true || schema["x-workerDock-secretReference"] === true) return false;
  const types = Array.isArray(schema.type) ? schema.type.filter((type: string) => type !== "null") : [schema.type];
  return types.length === 1 && ["string", "integer", "number"].includes(types[0]);
}

function matches(type: string, value: unknown): boolean {
  switch (type) {
    case "object": return value !== null && typeof value === "object" && !Array.isArray(value);
    case "array": return Array.isArray(value);
    case "string": return typeof value === "string";
    case "boolean": return typeof value === "boolean";
    case "null": return value === null;
    case "number": return typeof value === "number" && Number.isFinite(value);
    case "integer": return typeof value === "number" && Number.isSafeInteger(value);
    default: return false;
  }
}
function deepEqual(left: unknown, right: unknown): boolean { return JSON.stringify(left) === JSON.stringify(right); }
function multipleOf(value: number, divisor: number): boolean {
  const [a, ap] = decimal(value); const [b, bp] = decimal(divisor);
  if (b === 0n) return false;
  const scale = Math.max(ap, bp);
  const numerator = a * 10n ** BigInt(scale - ap);
  const denominator = b * 10n ** BigInt(scale - bp);
  return numerator % denominator === 0n;
}
function decimal(value: number): [bigint, number] {
  const [mantissa, exponentText] = value.toString().toLowerCase().split("e");
  const exponent = Number(exponentText ?? 0);
  const digits = mantissa.replace(".", "");
  const places = mantissa.includes(".") ? mantissa.length - mantissa.indexOf(".") - 1 : 0;
  const scale = places - exponent;
  return [BigInt(digits) * (scale < 0 ? 10n ** BigInt(-scale) : 1n), Math.max(0, scale)];
}
