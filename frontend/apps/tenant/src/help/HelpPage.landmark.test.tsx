import { render, screen } from "@testing-library/react";
import { expect, test } from "vitest";
import { HelpPage } from "./HelpPage";

test.each([
  ["home", undefined],
  ["article", "dashboard"],
  ["recovery", "missing-topic"],
] as const)("%s help view uses its host main landmark", (_view, slug) => {
  render(<main><HelpPage language="en" slug={slug} canManage onNavigate={() => {}} /></main>);
  expect(screen.getAllByRole("main")).toHaveLength(1);
  expect(screen.getByRole("heading", { level: 1 })).toBeInTheDocument();
});
