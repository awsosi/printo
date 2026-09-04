#!/usr/bin/env tsx
/**
 * Exports the built-in routing profiles to `profiles/`.
 *
 * The profile is authored once, in TypeScript, and both engines consume the exported JSON:
 * the worker imports the module directly, the Windows agent loads the file. Keeping a second
 * hand-written copy in C# would guarantee drift, and a rule set that differs between the
 * workstation and the server is the one failure mode nobody would notice until a parcel went
 * out with the courier copy stuck to it.
 *
 * `npm run profiles:export -w @printo/routing-engine` regenerates; `profiles.test.ts` fails
 * the build when the checked-in file no longer matches the source.
 */

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { BUILTIN_PROFILES, profileSlug } from '../src/profiles.js';

const here = dirname(fileURLToPath(import.meta.url));
const outputDir = resolve(here, '../../../profiles');

mkdirSync(outputDir, { recursive: true });

for (const profile of BUILTIN_PROFILES) {
  const path = resolve(outputDir, `${profileSlug(profile)}.json`);
  writeFileSync(path, `${JSON.stringify(profile, null, 2)}\n`, 'utf8');
  console.log(`wrote ${path}`);
}
