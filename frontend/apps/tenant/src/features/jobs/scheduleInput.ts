import type { ScheduleEditorInputDto } from "../schedules/ScheduleEditor";

export function sameScheduleInput(left: ScheduleEditorInputDto, right: ScheduleEditorInputDto): boolean {
  if (left.kind !== right.kind || left.cron !== right.cron || left.zoneId !== right.zoneId || left.localOnce !== right.localOnce) return false;
  if (left.selectedOffset === null || right.selectedOffset === null) return left.selectedOffset === right.selectedOffset;
  const leftOffset = normalizeOffset(left.selectedOffset);
  const rightOffset = normalizeOffset(right.selectedOffset);
  return leftOffset !== null && rightOffset !== null && leftOffset === rightOffset;
}

export function confirmsScheduleWrite(request: ScheduleEditorInputDto, persisted: ScheduleEditorInputDto): boolean {
  if (request.kind === "Once" && request.selectedOffset === null) {
    return request.kind === persisted.kind
      && request.cron === persisted.cron
      && request.zoneId === persisted.zoneId
      && request.localOnce === persisted.localOnce
      && persisted.selectedOffset !== null
      && normalizeOffset(persisted.selectedOffset) !== null;
  }
  return sameScheduleInput(request, persisted);
}

function normalizeOffset(value: string): bigint | null {
  const match = /^([+-])?(\d{1,2}):([0-5]\d)(?::([0-5]\d)(?:\.(\d{1,7}))?)?$/.exec(value);
  if (!match) return null;
  const sign = match[1] === "-" ? -1n : 1n;
  const ticks = BigInt(match[2]) * 36_000_000_000n + BigInt(match[3]) * 600_000_000n + BigInt(match[4] ?? "0") * 10_000_000n + BigInt((match[5] ?? "").padEnd(7, "0") || "0");
  return ticks === 0n ? 0n : sign * ticks;
}
