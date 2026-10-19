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
export function usePreferences(defaultTimeZone = "UTC") {
  const [hasLocalTimeZone, setHasLocalTimeZone] = useState(() => {
    try { return localStorage.getItem("workerdock.timeZone") !== null; } catch { return false; }
  });
  const [language, setLanguage] = useState<Language>(() =>
    stored("language", "en") === "pt-BR" ? "pt-BR" : "en",
  );
  const [timeZone, setTimeZone] = useState(() => {
    const value = stored("timeZone", defaultTimeZone);
    return timeZones.includes(value) ? value : defaultTimeZone;
  });
  useEffect(() => {
    document.documentElement.lang = language;
    try {
      localStorage.setItem("workerdock.language", language);
    } catch {}
  }, [language]);
  useEffect(() => {
    try {
      if (!hasLocalTimeZone && timeZones.includes(defaultTimeZone)) setTimeZone(defaultTimeZone);
    } catch {}
  }, [defaultTimeZone, hasLocalTimeZone]);
  useEffect(() => {
    if (hasLocalTimeZone) {
      try { localStorage.setItem("workerdock.timeZone", timeZone); } catch {}
    }
  }, [timeZone, hasLocalTimeZone]);
  const setUserTimeZone = (value: string) => {
    setTimeZone(timeZones.includes(value) ? value : "UTC");
    setHasLocalTimeZone(true);
  };
  return {
    language,
    setLanguage,
    timeZone,
    setTimeZone: setUserTimeZone,
    t: translations[language],
  };
}
