import { useEffect, useState } from "react";
import en from "../locales/en.json";
import pt from "../locales/pt-BR.json";
export type Language = "en" | "pt-BR";
export type Labels = typeof en;
export const translations: Record<Language, Labels> = { en, "pt-BR": pt };
function stored(key: string, fallback: string): string {
  try {
    return localStorage.getItem("workerdock." + key) ?? fallback;
  } catch {
    return fallback;
  }
}
export function usePreferences() {
  const [language, setLanguage] = useState<Language>(() =>
    stored("language", "en") === "pt-BR" ? "pt-BR" : "en",
  );
  useEffect(() => {
    document.documentElement.lang = language;
    try {
      localStorage.setItem("workerdock.language", language);
    } catch {}
  }, [language]);
  return {
    language,
    setLanguage,
    t: translations[language],
  };
}
