import { afterEach, expect, test, vi } from "vitest";
import { signOut } from "./client";

afterEach(() => vi.unstubAllGlobals());

test("sign-out preserves the protected provider return state in top-level navigation", async () => {
  const navigated: string[] = [];
  const target =
    "https://issuer.example/logout?post_logout_redirect_uri=https%3A%2F%2Flocalhost%2Fsignout-callback-oidc&state=protected%2Breturn%2Fstate%3D";
  vi.stubGlobal("window", {
    location: { assign: (url: string) => navigated.push(url) },
  });
  vi.stubGlobal("fetch", async (url: string, init?: RequestInit) => {
    if (url === "/api/antiforgery")
      return new Response(JSON.stringify({ requestToken: "server-csrf" }));
    const headers = new Headers(init?.headers);
    if (
      url !== "/auth/signout" ||
      init?.method !== "POST" ||
      init.credentials !== "same-origin" ||
      headers.get("X-WorkerDock-CSRF") !== "server-csrf"
    )
      return new Response("", { status: 400 });
    return new Response(JSON.stringify({ redirectUrl: target }));
  });
  await signOut();
  expect(navigated).toEqual([target]);
});

test("rejected sign-out never navigates away from the current session", async () => {
  const navigated: string[] = [];
  vi.stubGlobal("window", {
    location: { assign: (url: string) => navigated.push(url) },
  });
  vi.stubGlobal("fetch", async (url: string) =>
    url === "/api/antiforgery"
      ? new Response(JSON.stringify({ requestToken: "server-csrf" }))
      : new Response("", { status: 400 }),
  );
  await expect(signOut()).rejects.toMatchObject({ status: 400 });
  expect(navigated).toEqual([]);
});
