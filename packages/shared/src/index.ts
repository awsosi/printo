export const ROLES = {
  USER: 'USER',
  ADMIN: 'ADMIN'
} as const;

export type Role = (typeof ROLES)[keyof typeof ROLES];

export type ThemeMode = 'system' | 'light' | 'dark';

export interface JwtClaims {
  sub: string;
  username: string;
  roles: Role[];
  type: 'access' | 'refresh';
}

export { matchPdfPagesBySnippet, type PdfSnippetMatchPage, type PdfSnippetMatchResult } from './pdf-image-match.js';
