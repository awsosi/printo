/**
 * `@printo/routing-engine` — the shared routing contract.
 *
 * The rule schema, feature model, evaluation engine and placement maths live here so the
 * server worker and the Windows agent execute the same rules. The C# port in
 * `clients/windows/Printo.Agent.Core` mirrors these files one-for-one and is held to it by
 * the conformance suite in `tests/conformance`.
 */

export * from './features.js';
export * from './rules.js';
export * from './trace.js';
export * from './carrier.js';
export * from './predicates.js';
export * from './engine.js';
export * from './transform.js';
export * from './profiles.js';
export * from './conformance.js';
export * from './wire.js';
