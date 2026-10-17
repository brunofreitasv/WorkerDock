import { useEffect, useState } from "react";
import en from "../locales/en.json";
import pt from "../locales/pt-BR.json";
export type Language = "en" | "pt-BR";
export type Labels = typeof en;
export const translations: Record<Language, Labels> = { en, "pt-BR": pt };
export const timeZones = [
  ...new Set([
    "UTC",
    "America/Sao_Paulo",
    ...Intl.supportedValuesOf("timeZone"),
  ]),
];
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
  const [timeZone, setTimeZone] = useState(() => {
    const value = stored("timeZone", "UTC");
    return timeZones.includes(value) ? value : "UTC";
  });
  useEffect(() => {
    document.documentElement.lang = language;
    try {
      localStorage.setItem("workerdock.language", language);
    } catch {}
  }, [language]);
  useEffect(() => {
    try {
      localStorage.setItem("workerdock.timeZone", timeZone);
    } catch {}
  }, [timeZone]);
  return {
    language,
    setLanguage,
    timeZone,
    setTimeZone,
    t: translations[language],
  };
}
