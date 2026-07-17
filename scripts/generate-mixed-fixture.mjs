#!/usr/bin/env node
// Generates fixtures/intake/mixed-carriers.pdf — a mixed logistics document:
//   1. invoice (A4)            → DOCUMENT_A4          → A4 printer
//   2. DHL outgoing label 4×6  → OUTGOING_LABEL_THERMAL → thermal printer
//   3. packing slip (A4)       → DOCUMENT_A4          → A4 printer
//   4. UPS return label (A4)   → RETURN_LABEL_A4      → A4 printer (scaled)
//   5. FedEx outgoing label 4×6 → OUTGOING_LABEL_THERMAL → thermal printer
// Usage: node scripts/generate-mixed-fixture.mjs [output-path]
import { writeFile } from 'node:fs/promises';
import { PDFDocument, StandardFonts } from 'pdf-lib';
import { mixedCarrierPages } from './mixed-fixture-pages.mjs';

const outputPath = process.argv[2] ?? 'fixtures/intake/mixed-carriers.pdf';

const doc = await PDFDocument.create();
const font = await doc.embedFont(StandardFonts.Helvetica);

for (const pageSpec of mixedCarrierPages) {
  const page = doc.addPage([pageSpec.width, pageSpec.height]);
  let y = pageSpec.height - 40;
  for (const line of pageSpec.lines) {
    page.drawText(line, { x: 24, y, size: pageSpec.fontSize ?? 12, font });
    y -= (pageSpec.fontSize ?? 12) * 1.6;
  }
}

await writeFile(outputPath, await doc.save());
console.log(`wrote ${outputPath} (${mixedCarrierPages.length} pages)`);
