import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SearchResultText } from "./SearchResultText";

describe("documentation search result highlighting", () => {
  it("highlights matched terms as React text nodes, including accent-insensitive matches", () => {
    const { container } = render(<SearchResultText text="Operação e monitoramento" query="operacao monitoramento" />);
    expect(container.querySelectorAll("mark")).toHaveLength(2);
    expect(screen.getByText("Operação", { selector: "mark" })).toBeTruthy();
    expect(screen.getByText("monitoramento", { selector: "mark" })).toBeTruthy();
  });

  it("renders markup-like input as text rather than executable HTML", () => {
    const { container } = render(<SearchResultText text={'<img src=x onerror="alert(1)">'} query="img" />);
    expect(container.querySelector("img")).toBeNull();
    expect(screen.getByText("img", { selector: "mark" })).toBeTruthy();
  });
});
