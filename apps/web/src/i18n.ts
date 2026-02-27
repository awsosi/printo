import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export type I18nMessages = Record<string, string>;

const DEFAULT_LOCALE = 'en-US';
const localesDir = join(dirname(fileURLToPath(import.meta.url)), 'i18n');

function normalizeLocale(locale: string | undefined): string[] {
  if (!locale) {
    return [];
  }

  const normalized = locale.trim();
  if (!normalized) {
    return [];
  }

  const candidates = [normalized];
  if (normalized.includes('-')) {
    candidates.push(normalized.split('-')[0]);
  }

  return [...new Set(candidates)];
}

function readLocaleFile(locale: string): I18nMessages | null {
  const filePath = join(localesDir, `${locale}.json`);
  if (!existsSync(filePath)) {
    return null;
  }

  const parsed = JSON.parse(readFileSync(filePath, 'utf-8')) as unknown;
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return null;
  }

  return parsed as I18nMessages;
}

export function resolveMessages(preferredLocale: string | undefined): { locale: string; messages: I18nMessages } {
  const defaultMessages = readLocaleFile(DEFAULT_LOCALE) ?? {};

  for (const candidate of normalizeLocale(preferredLocale)) {
    const localized = readLocaleFile(candidate);
    if (localized) {
      return {
        locale: candidate,
        messages: { ...defaultMessages, ...localized }
      };
    }
  }

  return {
    locale: DEFAULT_LOCALE,
    messages: defaultMessages
  };
}

export function getDefaultLocale(): string {
  return DEFAULT_LOCALE;
}
