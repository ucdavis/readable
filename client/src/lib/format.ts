export function formatDateTime(iso: string) {
  const d = new Date(iso);
  return Number.isNaN(d.valueOf()) ? iso : d.toLocaleString();
}

export function formatBytes(bytes: number) {
  if (!Number.isFinite(bytes)) {
    return '';
  }
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ['KB', 'MB', 'GB', 'TB'] as const;
  let value = bytes / 1024;
  let unit: (typeof units)[number] = units[0];
  for (let i = 1; i < units.length && value >= 1024; i++) {
    value /= 1024;
    unit = units[i];
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${unit}`;
}

