export * from './types.js';
export { HeuristicPageClassifier, scorePageText } from './heuristic-classifier.js';
export { VisionServiceClassifier, CompositePageClassifier } from './vision-classifier.js';

import { HeuristicPageClassifier } from './heuristic-classifier.js';
import { CompositePageClassifier, VisionServiceClassifier } from './vision-classifier.js';
import type { PageClassifier } from './types.js';

/**
 * Env-driven classifier wiring:
 * - WORKER_CLASSIFIER=heuristic (default when no vision URL): text-layer rules only.
 * - WORKER_CLASSIFIER=vision: Vision Service required (errors surface as job failures).
 * - WORKER_CLASSIFIER=auto (default when WORKER_VISION_URL is set): vision with heuristic fallback.
 */
export function createDefaultPageClassifier(env: NodeJS.ProcessEnv = process.env): PageClassifier {
  const visionUrl = (env.WORKER_VISION_URL ?? '').trim();
  const mode = (env.WORKER_CLASSIFIER ?? (visionUrl ? 'auto' : 'heuristic')).toLowerCase();
  const timeoutMs = Number(env.WORKER_VISION_TIMEOUT_MS ?? 10_000);
  const heuristic = new HeuristicPageClassifier();

  if (mode === 'vision' && visionUrl) {
    return new VisionServiceClassifier({ baseUrl: visionUrl, timeoutMs });
  }

  if (mode === 'auto' && visionUrl) {
    return new CompositePageClassifier(new VisionServiceClassifier({ baseUrl: visionUrl, timeoutMs }), heuristic);
  }

  return heuristic;
}
