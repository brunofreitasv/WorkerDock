export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
  ) {
    super(code);
  }
}
export async function read<T>(
  path: string,
): Promise<{ data: T; etag: string | null }> {
  if (!path.startsWith("/api/")) throw new Error("InvalidApiPath");
  const response = await fetch(path, {
    credentials: "same-origin",
    headers: { Accept: "application/json" },
    cache: "no-store",
  });
  if (!response.ok) throw new ApiError(response.status, "RequestFailed");
  return {
    data: (await response.json()) as T,
    etag: response.headers.get("ETag"),
  };
}
export async function mutate<T>(
  path: string,
  method: "PUT" | "POST",
  body?: unknown,
  etag?: string,
): Promise<T> {
  if (!path.startsWith("/api/") && path !== "/auth/signout")
    throw new Error("InvalidApiPath");
  const { data } = await read<{ requestToken: string }>("/api/antiforgery");
  const headers: Record<string, string> = {
    "X-WorkerDock-CSRF": data.requestToken,
    "Content-Type": "application/json",
    Accept: "application/json",
  };
  if (etag) headers["If-Match"] = etag;
  const response = await fetch(path, {
    method,
    credentials: "same-origin",
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok)
    throw new ApiError(
      response.status,
      response.status === 412 ? "StaleVersion" : "RequestFailed",
    );
  return response.status === 204
    ? (undefined as T)
    : ((await response.json()) as T);
}
// The server invokes framework cookie/OIDC sign-out and returns its provider logout URL.
export async function signOut(): Promise<void> {
  const { data } = await read<{ requestToken: string }>("/api/antiforgery");
  const response = await fetch("/auth/signout", {
    method: "POST",
    credentials: "same-origin",
    headers: { "X-WorkerDock-CSRF": data.requestToken },
  });
  if (!response.ok) throw new ApiError(response.status, "RequestFailed");
  const result = (await response.json()) as { redirectUrl: string };
  window.location.assign(result.redirectUrl);
}
