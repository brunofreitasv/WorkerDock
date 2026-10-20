import { mutate } from "../../api/client";
import type { DynamicValuePlan } from "@workerdock/ui";

export type DynamicValuePreviewRequest = {
  templateId: string;
  templateVersion: string;
  configuration: Record<string, unknown>;
  dynamicValues: DynamicValuePlan;
  triggerKind: "Manual" | "Scheduled";
  scheduledForUtc: string | null;
  sampleAtUtc: string;
  referenceDateOverride: string | null;
  jobTimeout: string | null;
};

export type DynamicValuePreviewResponse = {
  issues: { path: string; code: string }[];
  values: Record<string, unknown>;
  isExample: boolean;
};

export function previewDynamicValues(
  request: DynamicValuePreviewRequest,
): Promise<DynamicValuePreviewResponse> {
  return mutate<DynamicValuePreviewResponse>(
    "/api/template-values/preview",
    "POST",
    request,
  );
}
