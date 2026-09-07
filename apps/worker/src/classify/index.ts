export * from './types.js';
export { HeuristicPageClassifier, scorePageText } from './heuristic-classifier.js';
export { VisionServiceClassifier, CompositePageClassifier } from './vision-classifier.js';
export { RoutingEngineClassifier } from './engine-classifier.js';
export { VisionFeatureSource, FeatureSourceUnavailableError } from './page-features.js';

import { HeuristicPageClassifier } from './heuristic-classifier.js';
import { RoutingEngineClassifier } from './engine-classifier.js';
import { VisionFeatureSource } from './page-features.js';
import { CompositePageClassifier, VisionServiceClassifier } from './vision-classifier.js';
import type { PageClassifier } from './types.js';

/**
 * Env-driven classifier wiring.
 *
 * - `WORKER_CLASSIFIER=engine` (the default when a vision URL is set): the shared routing
 *   engine, with the Vision Service measuring each page and the heuristic classifier as the
 *   fallback. This is the one path that runs the same rules as the Windows agent.
 * - `WORKER_CLASSIFIER=vision`: the Vision Service's own classifier. Kept for deployments
 *   that use a trained model behind that endpoint rather than the shared rule set.
 * - `WORKER_CLASSIFIER=auto`: vision classifier with the heuristic as fallback - what
 *   `auto` meant before the engine existed, kept so an upgrade changes nothing for a site
 *   that pinned it.
 * - `WORKER_CLASSIFIER=heuristic` (the default with no vision URL): text-layer rules only.
 *   It cannot see a label embedded in an A4 sheet, which is most of them, so it is a floor
 *   rather than a target.
 */
export function createDefaultPageClassifier(env: NodeJS.ProcessEnv = process.env): PageClassifier {
  const visionUrl = (env.WORKER_VISION_URL ?? '').trim();
  const mode = (env.WORKER_CLASSIFIER ?? (visionUrl ? 'engine' : 'heuristic')).toLowerCase();
  const timeoutMs = Number(env.WORKER_VISION_TIMEOUT_MS ?? 10_000);
  const heuristic = new HeuristicPageClassifier();

  if (mode === 'engine' && visionUrl) {
    return new RoutingEngineClassifier({
      // Measuring a page rasterizes it, which is slower than a text-layer classification and
      // deserves its own budget rather than the one sized for a JSON round trip.
      features: new VisionFeatureSource({
        baseUrl: visionUrl,
        timeoutMs: Number(env.WORKER_VISION_FEATURE_TIMEOUT_MS ?? Math.max(timeoutMs, 20_000))
      }),
      fallback: heuristic
    });
  }

  if (mode === 'vision' && visionUrl) {
    return new VisionServiceClassifier({ baseUrl: visionUrl, timeoutMs });
  }

  if (mode === 'auto' && visionUrl) {
    return new CompositePageClassifier(new VisionServiceClassifier({ baseUrl: visionUrl, timeoutMs }), heuristic);
  }

  return heuristic;
}
