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
      async () =>
        new Response(
          JSON.stringify({
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
  expect(screen.getByRole("link", { name: "Monitoring" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Help" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Audit" })).toBeVisible();
  expect(
    screen.queryByRole("link", { name: "Settings" }),
  ).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("link", { name: "Monitoring" }));
  expect(
    await screen.findByRole("heading", { name: "Monitoring" }),
  ).toBeVisible();
  await userEvent.click(screen.getByRole("link", { name: "Audit" }));
  expect(await screen.findByRole("heading", { name: "Audit" })).toBeVisible();
});

test("viewer opens a deep-linked help topic with Help selected and contextual dashboard help", async () => {
  window.history.replaceState(null, "", "#/help:dashboard");
  session();
  expect(await screen.findByRole("heading", { name: "Understand the dashboard" })).toBeVisible();
  expect(screen.getByRole("link", { name: "Help" })).toHaveAttribute("aria-current", "page");
  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Language" }), "pt-BR");
  expect(await screen.findByRole("heading", { name: "Entenda o painel" })).toBeVisible();
  expect(window.location.hash).toBe("#/help:dashboard");
});

test("dashboard contextual help opens its registered guide", async () => {
  window.history.replaceState(null, "", "#/dashboard");
  session();
  await userEvent.click(await screen.findByRole("button", { name: "Help for this page" }));
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
  await userEvent.click(await screen.findByRole("button", { name: "Help for this page" }));
  expect(screen.getAllByRole("heading", { name: "Monitor jobs and executions" }).length).toBeGreaterThan(0);
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
  await userEvent.click(await screen.findByRole("button", { name: "Help for this page" }));
  expect(screen.getAllByRole("heading", { name: "Manage SQL Server profiles" }).length).toBeGreaterThan(0);
  expect(window.location.hash).toBe("#/help:sql-profiles");
});

test.each(["#/help:unknown", "#/help:", "#/help:bad%ZZslug"])("recovers from malformed help route %s", async hash => {
  localStorage.setItem("workerdock.language", "en");
  window.history.replaceState(null, "", hash);
  session();
  expect(await screen.findByText("Unknown help topic")).toBeVisible();
  expect(screen.getByRole("link", { name: "Help" })).toHaveAttribute("aria-current", "page");
});
test("anonymous mode shows management and a translated public-access status", async () => {
  session(true, "Anonymous", false);
  expect(await screen.findByRole("link", { name: "Settings" })).toBeVisible();
  expect(screen.getByRole("status")).toHaveTextContent("Anonymous access");
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
  const name = await screen.findByRole("textbox", { name: "Display name" });
  await userEvent.clear(name);
  await userEvent.type(name, "Equipe");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(await screen.findByText("Settings saved")).toBeVisible();
});
