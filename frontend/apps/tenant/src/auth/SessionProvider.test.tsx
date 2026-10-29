import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import App from "../App";
import { SessionProvider } from "./SessionProvider";

afterEach(() => {
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", "#/dashboard");
  localStorage.removeItem("workerdock.language");
});
const session = (
  canManage = false,
  mode = "RoleProtected",
  authenticated = true,
) => {
  vi.stubGlobal(
    "fetch",
    vi.fn(
      async (url: string) =>
        new Response(
          JSON.stringify(url === "/api/settings" ? { displayName: "WorkerDock", displayTimeZone: "UTC", version: "v1" } : {
            authenticated,
            canRead: true,
            canManage,
            mode,
            language: "en",
            timeZone: "UTC",
          }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        ),
    ),
  );
  render(
    <SessionProvider>
      <App />
    </SessionProvider>,
  );
};
test("viewer can navigate dashboard monitoring and audit while management is hidden", async () => {
  session();
  expect(await screen.findByRole("link", { name: "Dashboard" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Job monitoring" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Documentation" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Audit" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Settings" })).toBeVisible();
  expect(screen.queryByRole("link", { name: "Jobs" })).not.toBeInTheDocument();
  expect(screen.getAllByRole("heading", { level: 2 }).map((heading) => heading.textContent)).toEqual(["Monitoring", "Maintenance", "Administration"]);
  expect(screen.getByRole("heading", { name: "Monitoring", level: 2 })).toBeVisible();
  await userEvent.click(screen.getByRole("link", { name: "Job monitoring" }));
  expect(
    await screen.findByRole("heading", { name: "Job monitoring", level: 1 }),
  ).toBeVisible();
  await userEvent.click(screen.getByRole("link", { name: "Audit" }));
  expect(await screen.findByRole("heading", { name: "Audit" })).toBeVisible();
});

test("viewer opens a deep-linked documentation topic with Documentation selected and contextual help", async () => {
  window.history.replaceState(null, "", "#/help:dashboard");
  session();
  expect(await screen.findByRole("heading", { name: "Understand the dashboard" })).toBeVisible();
  expect(screen.getAllByRole("link", { name: "Documentation" }).find(link => link.getAttribute("aria-current") === "page")).toBeDefined();
  expect(window.location.hash).toBe("#/help:dashboard");
  expect(screen.queryByRole("combobox", { name: "Language" })).not.toBeInTheDocument();
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("link", { name: "Settings" }));
  await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Language" }), "pt-BR");
  window.history.replaceState(null, "", "#/help:dashboard");
  window.dispatchEvent(new HashChangeEvent("hashchange"));
  expect(await screen.findByRole("heading", { name: "Entenda o painel" })).toBeVisible();
  expect(window.location.hash).toBe("#/help:dashboard");
});

test("dashboard contextual help opens its registered guide", async () => {
  window.history.replaceState(null, "", "#/dashboard");
  session();
  await userEvent.click(await screen.findByRole("button", { name: "Documentation for this page" }));
  expect(await screen.findByRole("heading", { name: "Understand the dashboard" })).toBeVisible();
  expect(window.location.hash).toBe("#/help:dashboard");
});

test("execution contextual help opens the monitoring guide", async () => {
  window.history.replaceState(null, "", "#/execution:run-1");
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const data = url === "/api/session"
      ? { authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC" }
      : { executionId: "run-1", jobId: "job-1", jobName: "Job", templateId: "template", state: "Succeeded", certainty: "Certain", triggerKind: "Manual", createdAtUtc: "2026-01-01T00:00:00Z", availableAtUtc: "2026-01-01T00:00:00Z", scheduledForUtc: null, correlationId: "corr-1", stopRequested: false, forceStop: false, attempts: [], timeline: [] };
    return new Response(JSON.stringify(data), { status: 200, headers: { "Content-Type": "application/json" } });
  }));
  render(<SessionProvider><App /></SessionProvider>);
  expect((await screen.findByRole("link", { name: "Job monitoring" })).getAttribute("aria-current")).toBe("page");
  expect(window.location.hash).toBe("#/execution:run-1");
  await userEvent.click(await screen.findByRole("button", { name: "Documentation for this page" }));
  expect(screen.getByRole("heading", { name: "Monitor executions", level: 1 })).toBeVisible();
  expect(window.location.hash).toBe("#/help:monitoring");
});

test("Profiles-tab contextual help opens the SQL profiles guide", async () => {
  window.history.replaceState(null, "", "#/settings");
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const data = url === "/api/session"
      ? { authenticated: true, canRead: true, canManage: true, mode: "RoleProtected", language: "en", timeZone: "UTC" }
      : url === "/api/settings"
        ? { displayName: "Team", displayTimeZone: "UTC", version: "v1" }
        : url === "/api/connection-profiles/"
          ? { items: [] }
          : {};
    return new Response(JSON.stringify(data), { status: 200, headers: { "Content-Type": "application/json" } });
  }));
  render(<SessionProvider><App /></SessionProvider>);
  await userEvent.click(await screen.findByRole("button", { name: "SQL profiles" }));
  await userEvent.click(await screen.findByRole("button", { name: "Documentation for this page" }));
  expect(screen.getAllByRole("heading", { name: "Manage SQL Server profiles" }).length).toBeGreaterThan(0);
  expect(window.location.hash).toBe("#/help:sql-profiles");
});

test.each(["#/help:unknown", "#/help:", "#/help:bad%ZZslug"])("recovers from malformed documentation route %s", async hash => {
  localStorage.setItem("workerdock.language", "en");
  window.history.replaceState(null, "", hash);
  session();
  expect(await screen.findByText("Unknown documentation topic")).toBeVisible();
  expect(screen.getAllByRole("link", { name: "Documentation" }).find(link => link.getAttribute("aria-current") === "page")).toBeDefined();
});
test("anonymous mode shows management and a translated public-access status", async () => {
  session(true, "Anonymous", false);
  expect(await screen.findByRole("link", { name: "Settings" })).toBeVisible();
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
  expect(screen.queryByText("Anyone who reaches this instance can view and manage its data.")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("link", { name: "Settings" }));
  expect(await screen.findByRole("status")).toHaveTextContent("Anonymous access");
  await userEvent.selectOptions(
    screen.getByRole("combobox", { name: "Language" }),
    "pt-BR",
  );
  expect(screen.getByRole("status")).toHaveTextContent("Acesso anônimo");
  expect(screen.getByRole("link", { name: "Configurações" })).toBeVisible();
  expect(localStorage.getItem("workerdock.language")).toBe("pt-BR");
  await userEvent.selectOptions(
    screen.getByRole("combobox", { name: "Fuso horário" }),
    "America/Sao_Paulo",
  );
  expect(localStorage.getItem("workerdock.timeZone")).toBe("America/Sao_Paulo");
  expect(Object.keys(localStorage).sort()).toEqual([
    "workerdock.language",
    "workerdock.timeZone",
  ]);
});
test("unauthenticated configured mode offers framework sign-in and hides protected navigation", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(
      async () =>
        new Response(
          JSON.stringify({
            authenticated: false,
            canRead: false,
            canManage: false,
            mode: "Authenticated",
            language: "en",
            timeZone: "UTC",
          }),
          { status: 200 },
        ),
    ),
  );
  render(
    <SessionProvider>
      <App />
    </SessionProvider>,
  );
  expect(await screen.findByRole("link", { name: "Sign in" })).toHaveAttribute(
    "href",
    "/auth/signin",
  );
  expect(
    screen.queryByRole("link", { name: "Dashboard" }),
  ).not.toBeInTheDocument();
});
test("session failure is safe and retryable", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => {
      throw new Error("sensitive upstream error");
    }),
  );
  render(
    <SessionProvider>
      <App />
    </SessionProvider>,
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Unable to load session",
  );
  expect(
    screen.queryByText("sensitive upstream error"),
  ).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Retry" })).toBeVisible();
});
test("skip navigation moves keyboard focus to main content", async () => {
  session();
  await screen.findByRole("link", { name: "Dashboard" });
  const skip = screen.getByRole("link", { name: "Skip to content" });
  skip.focus();
  await userEvent.keyboard("{Enter}");
  await waitFor(() => expect(screen.getByRole("main")).toHaveFocus());
});

test("manager edits settings using server version and antiforgery then sees saved result", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      if (url === "/api/session")
        return new Response(
          JSON.stringify({
            authenticated: true,
            canRead: true,
            canManage: true,
            mode: "RoleProtected",
            language: "en",
            timeZone: "UTC",
          }),
        );
      if (url === "/api/antiforgery")
        return new Response(JSON.stringify({ requestToken: "server-csrf" }));
      if (url === "/api/settings" && init?.method === "PUT") {
        const headers = new Headers(init.headers);
        if (
          headers.get("If-Match") !== '"version-1"' ||
          headers.get("X-WorkerDock-CSRF") !== "server-csrf" ||
          init.credentials !== "same-origin"
        )
          return new Response("{}", { status: 400 });
        return new Response(
          JSON.stringify({
            displayName: "Equipe",
            displayTimeZone: "UTC",
            version: "version-2",
          }),
          { headers: { ETag: '"version-2"' } },
        );
      }
      return new Response(
        JSON.stringify({
          displayName: "Team",
          displayTimeZone: "UTC",
          version: "version-1",
        }),
        { headers: { ETag: '"version-1"' } },
      );
    }),
  );
  render(
    <SessionProvider>
      <App />
    </SessionProvider>,
  );
  await userEvent.click(await screen.findByRole("link", { name: "Settings" }));
  await userEvent.click(await screen.findByRole("button", { name: "Tenant configuration" }));
  const name = await screen.findByRole("textbox", { name: "Display name" });
  await userEvent.clear(name);
  await userEvent.type(name, "Equipe");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(await screen.findByText("Settings saved")).toBeVisible();
});

test("reader settings are personal and management APIs are not requested", async () => {
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    calls.push(url);
    const data = url === "/api/session"
      ? { authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC" }
      : { displayName: "Team North", displayTimeZone: "UTC", version: "v1" };
    return new Response(JSON.stringify(data), { status: 200, headers: { ETag: '"v1"', "Content-Type": "application/json" } });
  }));
  window.history.replaceState(null, "", "#/settings");
  render(<SessionProvider><App /></SessionProvider>);
  expect(await screen.findByRole("button", { name: "Personal settings" })).toBeVisible();
  expect(screen.getByRole("status")).toHaveTextContent("Role-protected management");
  expect(screen.getByRole("combobox", { name: "Language" })).toBeVisible();
  expect(screen.queryByRole("button", { name: "Tenant configuration" })).not.toBeInTheDocument();
  expect(screen.getByText("Team North")).toBeVisible();
  expect(document.title).toBe("Team North");
  expect(calls).toContain("/api/settings");
  expect(calls.some((url) => /vault|outbound|connection-profiles|retention/.test(url))).toBe(false);
});

test("settings load failures keep the WorkerDock name and navigation usable", async () => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => url === "/api/settings"
    ? new Response("{}", { status: 503 })
    : new Response(JSON.stringify({ authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC" }), { status: 200 })));
  render(<SessionProvider><App /></SessionProvider>);
  expect(await screen.findByRole("link", { name: "Settings" })).toBeVisible();
  expect(await screen.findByText("WorkerDock")).toBeVisible();
  expect(document.title).toBe("WorkerDock");
});

test("save success updates the app branding; stale save preserves the persisted name", async () => {
  let failPut = false;
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    if (url === "/api/session") return new Response(JSON.stringify({ authenticated: true, canRead: true, canManage: true, mode: "RoleProtected", language: "en", timeZone: "UTC" }));
    if (url === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    if (url === "/api/settings" && init?.method === "PUT") return failPut ? new Response("{}", { status: 412 }) : new Response(JSON.stringify({ displayName: "New Name", displayTimeZone: "UTC", version: "v2" }));
    return new Response(JSON.stringify({ displayName: "Team", displayTimeZone: "UTC", version: "v1" }), { headers: { ETag: '"v1"' } });
  }));
  window.history.replaceState(null, "", "#/settings");
  render(<SessionProvider><App /></SessionProvider>);
  await userEvent.click(await screen.findByRole("button", { name: "Tenant configuration" }));
  const name = await screen.findByRole("textbox", { name: "Display name" });
  await userEvent.clear(name); await userEvent.type(name, "New Name");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(await screen.findByText("New Name")).toBeVisible();
  await waitFor(() => expect(document.title).toBe("New Name"));
  failPut = true;
  await userEvent.clear(name); await userEvent.type(name, "Discarded");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(await screen.findByText("Settings changed. Reload before saving again.")).toBeVisible();
  expect(screen.getAllByText("New Name").length).toBeGreaterThan(0);
  expect(document.title).toBe("New Name");
});
