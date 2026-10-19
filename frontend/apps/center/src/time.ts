export function formatDistanceToNow(value?: string | null): string {
  if (!value) return 'Never';
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) return 'Unknown';
  const seconds = Math.round((timestamp - Date.now()) / 1000);
  const absolute = Math.abs(seconds);
  if (absolute < 60) return seconds >= 0 ? 'just now' : `${absolute} seconds ago`;
  const minutes = Math.round(absolute / 60);
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'} ${seconds < 0 ? 'ago' : 'from now'}`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ${seconds < 0 ? 'ago' : 'from now'}`;
  const days = Math.round(hours / 24);
  return `${days} day${days === 1 ? '' : 's'} ${seconds < 0 ? 'ago' : 'from now'}`;
}
