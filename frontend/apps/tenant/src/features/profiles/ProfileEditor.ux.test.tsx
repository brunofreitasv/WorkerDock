import { render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { ProfileEditor } from "./ProfileEditor";

vi.mock("../../api/client", () => ({
  ApiError: class extends Error {},
  read: vi.fn(async () => ({ data: { items: [] } })),
  mutate: vi.fn(),
}));

test("profiles separate inventory, connection, authentication, and actions", async () => {
  render(<ProfileEditor />);
  expect(await screen.findByRole("region", { name: "SQL Server profiles" })).toBeInTheDocument();
  expect(screen.getByRole("region", { name: "New SQL Server profile" })).toBeInTheDocument();
  expect(screen.getByRole("group", { name: "Connection" })).toBeInTheDocument();
  expect(screen.getByRole("group", { name: "Authentication" })).toBeInTheDocument();
  expect(screen.getByRole("group", { name: "Profile actions" })).toContainElement(screen.getByRole("button", { name: "Save profile" }));
});

test("profile form groups follow pt-BR", async () => {
  render(<ProfileEditor language="pt-BR" />);
  expect(await screen.findByRole("region", { name: "SQL Server profiles" })).toBeInTheDocument();
  expect(screen.getByRole("group", { name: "Conexão" })).toBeInTheDocument();
  expect(screen.getByRole("group", { name: "Autenticação" })).toBeInTheDocument();
  expect(screen.getByRole("group", { name: "Ações do perfil" })).toBeInTheDocument();
});

test("empty profile inventory explains the next step in both languages", async () => {
  const { unmount } = render(<ProfileEditor />);
  expect(await screen.findByText("No SQL Server profiles yet.")).toBeVisible();
  unmount();
  render(<ProfileEditor language="pt-BR" />);
  expect(await screen.findByText("Nenhum perfil do SQL Server ainda.")).toBeVisible();
});
