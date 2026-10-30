export function formatDistanceToNow(value?: string | null, locale: 'en' | 'pt-BR' = 'en'): string {
  if (!value) return locale === 'pt-BR' ? 'Nunca' : 'Never';
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) return locale === 'pt-BR' ? 'Desconhecido' : 'Unknown';
  const seconds = Math.round((timestamp - Date.now()) / 1000);
  const absolute = Math.abs(seconds);
  if (locale === 'pt-BR') {
    const relative = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
    if (absolute < 60) return relative.format(seconds, 'second');
    const minutes = Math.round(seconds / 60);
    if (Math.abs(minutes) < 60) return relative.format(minutes, 'minute');
    const hours = Math.round(seconds / 3600);
    if (Math.abs(hours) < 24) return relative.format(hours, 'hour');
    return relative.format(Math.round(seconds / 86400), 'day');
  }
  if (absolute < 60) return seconds >= 0 ? 'just now' : `${absolute} seconds ago`;
  const minutes = Math.round(absolute / 60);
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'} ${seconds < 0 ? 'ago' : 'from now'}`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ${seconds < 0 ? 'ago' : 'from now'}`;
  const days = Math.round(hours / 24);
  return `${days} day${days === 1 ? '' : 's'} ${seconds < 0 ? 'ago' : 'from now'}`;
}
