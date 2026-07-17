import { PDFDocument } from 'pdf-lib';

/**
 * Extracts a single page from a PDF as a standalone one-page PDF.
 * Pure-JS (pdf-lib) — no native dependencies. Returns null when the input is
 * not parseable as a PDF or the page number is out of range.
 */
export async function extractSinglePagePdf(pdfBuffer: Buffer, pageNumber: number): Promise<Buffer | null> {
  try {
    const source = await PDFDocument.load(pdfBuffer, { ignoreEncryption: true });
    if (pageNumber < 1 || pageNumber > source.getPageCount()) {
      return null;
    }
    const target = await PDFDocument.create();
    const [page] = await target.copyPages(source, [pageNumber - 1]);
    target.addPage(page);
    return Buffer.from(await target.save());
  } catch {
    return null;
  }
}
