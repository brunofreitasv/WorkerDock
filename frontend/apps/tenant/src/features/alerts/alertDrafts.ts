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
