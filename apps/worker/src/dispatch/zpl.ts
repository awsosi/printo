/**
 * Minimal ZPL II generation for Zebra-class thermal printers.
 * Used for raw dispatch through CUPS raw queues (`lp -o raw`).
 */

export interface ZplTextElement {
  kind: 'text';
  x: number;
  y: number;
  text: string;
  fontHeight?: number;
  fontWidth?: number;
}

export interface ZplBarcode128Element {
  kind: 'barcode128';
  x: number;
  y: number;
  value: string;
  height?: number;
  printHumanReadable?: boolean;
}

export interface ZplBoxElement {
  kind: 'box';
  x: number;
  y: number;
  width: number;
  height: number;
  thickness?: number;
}

export type ZplElement = ZplTextElement | ZplBarcode128Element | ZplBoxElement;

export interface ZplLabelInput {
  /** Label size in printer dots (203 dpi 4x6in label = 812 x 1218). */
  widthDots: number;
  heightDots: number;
  elements: ZplElement[];
}

function escapeZplText(value: string): string {
  // ^ and ~ are ZPL control prefixes; substitute lookalikes to avoid injection.
  return value.replace(/\^/g, '＾').replace(/~/g, '～');
}

export function buildZplLabel(input: ZplLabelInput): string {
  const lines: string[] = ['^XA', `^PW${Math.round(input.widthDots)}`, `^LL${Math.round(input.heightDots)}`, '^LH0,0'];

  for (const element of input.elements) {
    if (element.kind === 'text') {
      const height = Math.round(element.fontHeight ?? 30);
      const width = Math.round(element.fontWidth ?? height);
      lines.push(`^FO${Math.round(element.x)},${Math.round(element.y)}^A0N,${height},${width}^FD${escapeZplText(element.text)}^FS`);
    } else if (element.kind === 'barcode128') {
      const height = Math.round(element.height ?? 100);
      const readable = element.printHumanReadable === false ? 'N' : 'Y';
      lines.push(
        `^FO${Math.round(element.x)},${Math.round(element.y)}^BCN,${height},${readable},N,N^FD${escapeZplText(element.value)}^FS`
      );
    } else {
      const thickness = Math.round(element.thickness ?? 2);
      lines.push(
        `^FO${Math.round(element.x)},${Math.round(element.y)}^GB${Math.round(element.width)},${Math.round(element.height)},${thickness}^FS`
      );
    }
  }

  lines.push('^XZ');
  return lines.join('\n');
}

export interface GrayBitmap {
  width: number;
  height: number;
  /** Row-major 8-bit grayscale, 0 = black, 255 = white. */
  pixels: Uint8Array;
}

/**
 * Encodes a grayscale bitmap as a ZPL ^GFA graphic field (1-bit threshold dither).
 * Lets a rasterized label page (e.g. produced by the Vision Service) be embedded
 * in a generated label without any printer driver.
 */
export function grayBitmapToZplGraphicField(bitmap: GrayBitmap, threshold = 128): string {
  const bytesPerRow = Math.ceil(bitmap.width / 8);
  const totalBytes = bytesPerRow * bitmap.height;
  const hexRows: string[] = [];

  for (let y = 0; y < bitmap.height; y += 1) {
    const row = new Uint8Array(bytesPerRow);
    for (let x = 0; x < bitmap.width; x += 1) {
      const isBlack = (bitmap.pixels[y * bitmap.width + x] ?? 255) < threshold;
      if (isBlack) {
        row[Math.floor(x / 8)]! |= 0x80 >> x % 8;
      }
    }
    hexRows.push(Buffer.from(row).toString('hex').toUpperCase());
  }

  return `^GFA,${totalBytes},${totalBytes},${bytesPerRow},${hexRows.join('')}`;
}

/** True when a payload already looks like raw ZPL (starts with ^XA or ~ command). */
export function looksLikeZpl(payload: Buffer | string): boolean {
  const head = (Buffer.isBuffer(payload) ? payload.subarray(0, 64).toString('utf8') : payload.slice(0, 64)).trimStart();
  return head.startsWith('^XA') || head.startsWith('~');
}
