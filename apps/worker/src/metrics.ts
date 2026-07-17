/**
 * Minimal Prometheus text-format metrics for the worker — no external deps.
 * Exposed at GET /metrics; scraped by any Prometheus-compatible collector.
 */

type LabelValues = Record<string, string>;

function labelKey(labels: LabelValues): string {
  const entries = Object.entries(labels).sort(([a], [b]) => a.localeCompare(b));
  return entries.map(([name, value]) => `${name}="${value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`).join(',');
}

class Counter {
  private readonly values = new Map<string, number>();

  constructor(
    public readonly name: string,
    public readonly help: string
  ) {}

  inc(labels: LabelValues = {}, amount = 1): void {
    const key = labelKey(labels);
    this.values.set(key, (this.values.get(key) ?? 0) + amount);
  }

  render(): string {
    const lines = [`# HELP ${this.name} ${this.help}`, `# TYPE ${this.name} counter`];
    if (this.values.size === 0) {
      lines.push(`${this.name} 0`);
    }
    for (const [key, value] of this.values) {
      lines.push(key ? `${this.name}{${key}} ${value}` : `${this.name} ${value}`);
    }
    return lines.join('\n');
  }

  reset(): void {
    this.values.clear();
  }
}

class Histogram {
  private readonly bucketCounts: number[];
  private count = 0;
  private sum = 0;

  constructor(
    public readonly name: string,
    public readonly help: string,
    private readonly buckets: number[]
  ) {
    this.bucketCounts = buckets.map(() => 0);
  }

  observe(value: number): void {
    this.count += 1;
    this.sum += value;
    for (let index = 0; index < this.buckets.length; index += 1) {
      if (value <= this.buckets[index]!) {
        this.bucketCounts[index]! += 1;
      }
    }
  }

  render(): string {
    const lines = [`# HELP ${this.name} ${this.help}`, `# TYPE ${this.name} histogram`];
    for (let index = 0; index < this.buckets.length; index += 1) {
      lines.push(`${this.name}_bucket{le="${this.buckets[index]}"} ${this.bucketCounts[index]}`);
    }
    lines.push(`${this.name}_bucket{le="+Inf"} ${this.count}`);
    lines.push(`${this.name}_sum ${this.sum}`);
    lines.push(`${this.name}_count ${this.count}`);
    return lines.join('\n');
  }

  reset(): void {
    this.bucketCounts.fill(0);
    this.count = 0;
    this.sum = 0;
  }
}

export class WorkerMetrics {
  readonly jobsTotal = new Counter('printo_jobs_total', 'Print jobs finished, by final status.');
  readonly filesProcessedTotal = new Counter('printo_files_processed_total', 'Source files fully processed.');
  readonly pagesClassifiedTotal = new Counter(
    'printo_pages_classified_total',
    'Pages classified, by page class and classifier backend.'
  );
  readonly pagesRoutedTotal = new Counter(
    'printo_pages_routed_total',
    'Pages routed, by route type and deciding rule.'
  );
  readonly pageDispatchTotal = new Counter(
    'printo_page_dispatch_total',
    'Per-page printer submissions, by route type and outcome.'
  );
  readonly classificationConfidence = new Histogram(
    'printo_classification_confidence',
    'Confidence distribution of page classifications.',
    [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1]
  );

  render(): string {
    return (
      [
        this.jobsTotal.render(),
        this.filesProcessedTotal.render(),
        this.pagesClassifiedTotal.render(),
        this.pagesRoutedTotal.render(),
        this.pageDispatchTotal.render(),
        this.classificationConfidence.render()
      ].join('\n') + '\n'
    );
  }

  reset(): void {
    this.jobsTotal.reset();
    this.filesProcessedTotal.reset();
    this.pagesClassifiedTotal.reset();
    this.pagesRoutedTotal.reset();
    this.pageDispatchTotal.reset();
    this.classificationConfidence.reset();
  }
}

/** Process-wide registry used by the pipeline and the /metrics endpoint. */
export const workerMetrics = new WorkerMetrics();
