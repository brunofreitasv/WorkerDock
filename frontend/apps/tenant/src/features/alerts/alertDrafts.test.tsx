import { expect, test } from "vitest";
import { createSinkDraft, sinkPayload } from "./alertDrafts";

test("creates a webhook sink with safe defaults", () => {
  expect(createSinkDraft("sink-id")).toEqual({
    id: "sink-id",
    name: "",
    type: "webhook",
    configuration: { url: "", headers: [] },
    maxAttempts: 5,
    retryBaseDelaySeconds: 10,
    retryMaxDelaySeconds: 120,
    timeoutSeconds: 15,
    enabled: true,
    version: "",
  });
});

test("sink payload removes server response header values", () => {
  const draft = {
    ...createSinkDraft("sink-id"),
    configuration: { url: "https://example.test", headers: [{ name: "Authorization", secretReference: "vault/token", value: "response-only" }] },
  };
  expect(sinkPayload(draft)).toEqual({
    id: "sink-id",
    name: "",
    type: "webhook",
    configuration: { url: "https://example.test", headers: [{ name: "Authorization", secretReference: "vault/token" }] },
    maxAttempts: 5,
    retryBaseDelaySeconds: 10,
    retryMaxDelaySeconds: 120,
    timeoutSeconds: 15,
    enabled: true,
  });
});
