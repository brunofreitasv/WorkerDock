export interface AlertSink {
  id: string;
  name: string;
  type: string;
  configuration: {
    url: string;
    headers: Array<{ name: string; secretReference: string; value?: string }>;
  };
  maxAttempts: number;
  retryBaseDelaySeconds: number;
  retryMaxDelaySeconds: number;
  timeoutSeconds: number;
  enabled: boolean;
  version: string;
}

export interface AlertRule {
  id: string; name: string; condition: string; threshold: number; windowSeconds: number;
  graceSeconds: number; reminderSeconds: number; sinkId: string; enabled: boolean; version: string;
}

export function createRuleDraft(id: string, sinkId: string): AlertRule {
  return { id, name: "", condition: "RepeatedFailure", threshold: 3, windowSeconds: 300, graceSeconds: 0, reminderSeconds: 0, sinkId, enabled: false, version: "" };
}

export function rulePayload(draft: AlertRule): Omit<AlertRule, "version"> {
  const { version: _version, ...payload } = draft;
  return payload;
}

export function createSinkDraft(id: string): AlertSink {
  return {
    id,
    name: "",
    type: "webhook",
    configuration: { url: "", headers: [] },
    maxAttempts: 5,
    retryBaseDelaySeconds: 10,
    retryMaxDelaySeconds: 120,
    timeoutSeconds: 15,
    enabled: true,
    version: "",
  };
}

export function sinkPayload(draft: AlertSink): Omit<AlertSink, "version"> {
  const { version: _version, configuration, ...sink } = draft;
  return {
    ...sink,
    configuration: {
      url: configuration.url,
      headers: configuration.headers.map(({ name, secretReference }) => ({ name, secretReference })),
    },
  };
}
