import type { DynamicValueBinding, DynamicValuePlan } from "./dynamicValueTypes";

export function bindingAt(plan: DynamicValuePlan | null | undefined, target: string): DynamicValueBinding | undefined {
  return plan?.bindings.find(binding => binding.target === target);
}

export function setBinding(plan: DynamicValuePlan | null | undefined, binding: DynamicValueBinding | null, target: string, timeZoneId = "UTC"): DynamicValuePlan | null {
  const bindings = (plan?.bindings ?? []).filter(item => item.target !== target);
  if (binding) bindings.push({ ...binding, target });
  return bindings.length ? { resolverVersion: 1, timeZoneId: plan?.timeZoneId ?? timeZoneId, bindings } : null;
}

export function removeArrayItem(plan: DynamicValuePlan | null, arrayPointer: string, index: number): DynamicValuePlan | null {
  const parent = decodePointer(arrayPointer);
  if (!parent || !Number.isSafeInteger(index) || index < 0) return plan;
  const bindings = (plan?.bindings ?? []).flatMap(binding => {
    const segments = decodePointer(binding.target);
    if (!segments || segments.length <= parent.length || !samePrefix(segments, parent)) return [binding];
    const row = canonicalIndex(segments[parent.length]);
    if (row === null || row < index) return [binding];
    if (row === index) return [];
    segments[parent.length] = String(row - 1);
    return [{ ...binding, target: encodePointer(segments) }];
  });
  return bindings.length ? { resolverVersion: 1, timeZoneId: plan?.timeZoneId ?? "UTC", bindings } : null;
}

export function renameMapEntry(plan: DynamicValuePlan | null, parentPointer: string, oldKey: string, newKey: string): DynamicValuePlan | null {
  const parent = decodePointer(parentPointer);
  if (!parent || isUnsafe(newKey)) return plan;
  const bindings = (plan?.bindings ?? []).map(binding => {
    const segments = decodePointer(binding.target);
    if (!segments || segments.length <= parent.length || !samePrefix(segments, parent) || segments[parent.length] !== oldKey) return binding;
    segments[parent.length] = newKey;
    return { ...binding, target: encodePointer(segments) };
  });
  return bindings.length ? { resolverVersion: 1, timeZoneId: plan?.timeZoneId ?? "UTC", bindings } : null;
}

export function removeMapEntry(plan: DynamicValuePlan | null, parentPointer: string, key: string): DynamicValuePlan | null {
  const parent = decodePointer(parentPointer);
  if (!parent) return plan;
  const bindings = (plan?.bindings ?? []).filter(binding => {
    const segments = decodePointer(binding.target);
    return !segments || segments.length <= parent.length || !samePrefix(segments, parent) || segments[parent.length] !== key;
  });
  return bindings.length ? { resolverVersion: 1, timeZoneId: plan?.timeZoneId ?? "UTC", bindings } : null;
}

export function encodePointerSegment(segment: string): string { return segment.replaceAll("~", "~0").replaceAll("/", "~1"); }
export function decodePointer(pointer: string): string[] | null {
  if (!pointer.startsWith("/") || pointer.length > 256) return null;
  const result = pointer.slice(1).split("/").map(segment => {
    if (/~(?![01])/.test(segment)) return null;
    return segment.replaceAll("~1", "/").replaceAll("~0", "~");
  });
  return result.some(segment => segment === null || isUnsafe(segment)) ? null : result as string[];
}
export function encodePointer(segments: string[]): string { return "/" + segments.map(encodePointerSegment).join("/"); }
function samePrefix(segments: string[], prefix: string[]): boolean { return prefix.every((segment, index) => segments[index] === segment); }
function canonicalIndex(value: string): number | null { return /^(0|[1-9][0-9]*)$/.test(value) && Number.isSafeInteger(Number(value)) ? Number(value) : null; }
function isUnsafe(value: string): boolean { return value === "__proto__" || value === "prototype" || value === "constructor"; }
