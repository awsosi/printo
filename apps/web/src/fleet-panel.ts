/**
 * The fleet tab of the admin console.
 *
 * Its own module rather than more lines in `app.ts`, which is already a single 2800-line
 * template. The markup and the browser script are returned as strings and injected into that
 * template, so the tab shares its stylesheet, its auth token and its `request` helper - one
 * console, one login - without the page itself growing another thousand lines.
 *
 * Four questions this tab has to answer, in the order an administrator asks them:
 *
 *   1. *Which machines do I have, and are they alive?*  - the agent list.
 *   2. *Which rules are they running?*                  - the bundle publisher.
 *   3. *What did they have to ask a person about?*      - fallback analytics.
 *   4. *What should I fix first?*                       - the review queue.
 */

/** The fleet tab's markup, injected into the admin page's tab panels. */
export function fleetPanelHtml(): string {
  return `
          <section id="panel-fleet" class="panel">
            <div class="grid2">
              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Agents</h2>
                    <p class="muted">Workstations running the Printo agent. Disabling one cuts it off on its next request.</p>
                  </div>
                  <span class="pill" id="fleetAgentCount">0 agents</span>
                </div>
                <div id="fleetAgentList" class="status-table"></div>
                <form id="fleetTokenForm" class="stack">
                  <div class="grid2">
                    <label>Label
                      <input id="fleetTokenLabel" type="text" placeholder="Packing bench 3" />
                    </label>
                    <label>Valid for (hours)
                      <input id="fleetTokenHours" type="number" min="1" max="720" value="24" />
                    </label>
                  </div>
                  <div class="grid2">
                    <label>Uses
                      <input id="fleetTokenUses" type="number" min="1" max="500" value="1" />
                    </label>
                    <button type="submit">Issue enrolment token</button>
                  </div>
                  <p class="hint muted">Shown once. The installer writes it to <code>enrollment.token</code> beside the agent configuration.</p>
                  <div id="fleetTokenResult" class="status muted"></div>
                </form>
              </section>

              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Rule bundle</h2>
                    <p class="muted">What every agent downloads and executes. Validated here before it reaches a single machine.</p>
                  </div>
                  <span class="pill" id="fleetBundleVersion">none published</span>
                </div>
                <label>Bundle JSON
                  <textarea id="fleetBundleEditor" rows="16" spellcheck="false"></textarea>
                </label>
                <div class="grid2">
                  <label>Notes
                    <input id="fleetBundleNotes" type="text" placeholder="Added the DPD label rule" />
                  </label>
                  <button id="fleetPublishButton" type="button">Publish to the fleet</button>
                </div>
                <button id="fleetLoadBundleButton" type="button" class="secondary">Reload the published bundle</button>
                <div id="fleetBundleStatus" class="status muted"></div>
              </section>
            </div>

            <div class="grid2">
              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Template from a sample</h2>
                    <p class="muted">Open a sample document, drag a box round the logo, and get a rule that matches it.</p>
                  </div>
                  <span class="pill" id="fleetSampleInfo">no sample</span>
                </div>
                <div class="grid2">
                  <label>Sample PDF
                    <input id="fleetSampleFile" type="file" accept="application/pdf" />
                  </label>
                  <label>Page
                    <input id="fleetSamplePage" type="number" min="1" value="1" />
                  </label>
                </div>
                <div id="fleetSampleStage" style="position:relative;overflow:auto;max-height:420px;border:1px solid var(--line);border-radius:8px;">
                  <canvas id="fleetSampleCanvas" style="display:block;max-width:100%;cursor:crosshair;"></canvas>
                  <div id="fleetSampleSelection" style="position:absolute;display:none;border:2px solid var(--accent);background:rgba(29,90,72,0.14);pointer-events:none;"></div>
                </div>
                <div class="grid2">
                  <label>Template name
                    <input id="fleetTemplateName" type="text" placeholder="dhl-logo" />
                  </label>
                  <label>Match threshold
                    <input id="fleetTemplateThreshold" type="number" min="0" max="1" step="0.05" value="0.8" />
                  </label>
                </div>
                <button id="fleetAddTemplateButton" type="button">Add the template and a rule to the bundle</button>
                <p class="hint muted">The cutting is done here in the browser; the document is never uploaded.</p>
                <div id="fleetTemplateStatus" class="status muted"></div>
              </section>
            </div>

            <div class="grid2">
              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Fallbacks</h2>
                    <p class="muted">Where the engine had to ask a person. Driving these to zero is the point of the rules.</p>
                  </div>
                  <span class="pill" id="fleetFallbackTotal">0 events</span>
                </div>
                <div id="fleetFallbackSummary" class="status-table"></div>
                <div id="fleetFallbackList" class="status-table"></div>
              </section>

              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Recent jobs</h2>
                    <p class="muted">Open one to see where each page went, and what decided its size.</p>
                  </div>
                  <span class="pill" id="fleetJobCount">0 jobs</span>
                </div>
                <div id="fleetJobList" class="status-table"></div>
                <div id="fleetJobDetail" class="status-table"></div>
              </section>

              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Accounting</h2>
                    <p class="muted">Pages printed in the last 30 days, by machine, user and destination.</p>
                  </div>
                  <span class="pill" id="fleetAccountingTotal">0 pages</span>
                </div>
                <div id="fleetReconciliation" class="status muted"></div>
                <div id="fleetAccountingList" class="status-table"></div>
              </section>

              <section class="card stack">
                <div class="section-title">
                  <div class="stack">
                    <h2>Review queue</h2>
                    <p class="muted">Answered fallbacks waiting to become a rule.</p>
                  </div>
                  <span class="pill" id="fleetReviewCount">0 open</span>
                </div>
                <div id="fleetReviewList" class="status-table"></div>
              </section>
            </div>
          </section>`;
}

/**
 * The fleet tab's browser script.
 *
 * Written in the same plain-ES5-ish style as the rest of the page's inline script, and it
 * reuses that script's `request`, `escapeHtml`, `byId` and `formatRelativeTime` helpers
 * rather than redefining them.
 */
export function fleetPanelScript(): string {
  return `
        const fleet = {
          agents: [],
          bundle: null,
          summary: [],
          fallbacks: [],
          review: [],
          accounting: null,
          jobs: [],
          jobDetail: null
        };

        function fleetSetStatus(id, message, tone) {
          const target = byId(id);
          if (!target) {
            return;
          }
          target.textContent = message || '';
          target.className = 'status ' + (tone || 'muted');
        }

        function fleetAgo(value) {
          return value ? formatRelativeTime(value) : 'never';
        }

        /** True when an agent has not been heard from for long enough to be worth flagging. */
        function fleetIsStale(agent) {
          if (!agent.lastSeenAt) {
            return true;
          }
          // Agents heartbeat once a minute; ten minutes of silence is a machine that is off,
          // asleep or cut off from the server, and an administrator wants to see which.
          return Date.now() - new Date(agent.lastSeenAt).getTime() > 10 * 60 * 1000;
        }

        function renderFleetAgents() {
          const target = byId('fleetAgentList');
          byId('fleetAgentCount').textContent = fleet.agents.length + (fleet.agents.length === 1 ? ' agent' : ' agents');

          if (!fleet.agents.length) {
            target.innerHTML = '<div class="item muted">No machine has enrolled yet. Issue a token below and install the agent.</div>';
            return;
          }

          target.innerHTML = fleet.agents.map(function (agent) {
            const stale = fleetIsStale(agent) && agent.status === 'ACTIVE';
            const tone = agent.status !== 'ACTIVE' ? 'danger' : stale ? 'warn' : 'ok';
            const modes = ['local', 'server', 'auto'].map(function (mode) {
              return '<option value="' + mode + '"' + (agent.decisionMode === mode ? ' selected' : '') + '>' + mode + '</option>';
            }).join('');

            return '<div class="item stack" data-agent="' + escapeHtml(agent.id) + '">' +
              '<div class="section-title">' +
                '<strong>' + escapeHtml(agent.machineName) + '</strong>' +
                '<span class="pill ' + tone + '">' + escapeHtml(agent.status) + (stale ? ' · quiet' : '') + '</span>' +
              '</div>' +
              '<p class="muted">Last seen ' + escapeHtml(fleetAgo(agent.lastSeenAt)) +
                ' · agent ' + escapeHtml(agent.agentVersion || 'unknown') +
                ' · bundle ' + escapeHtml(agent.bundleVersion == null ? 'built-in' : String(agent.bundleVersion)) +
                (agent.lastUser ? ' · ' + escapeHtml(agent.lastUser) : '') + '</p>' +
              '<div class="grid2">' +
                '<label>Decision mode<select data-agent-mode="' + escapeHtml(agent.id) + '">' + modes + '</select></label>' +
                '<label>Confidence threshold<input type="number" min="0" max="1" step="0.05" value="' +
                  escapeHtml(String(agent.confidenceThreshold)) + '" data-agent-threshold="' + escapeHtml(agent.id) + '" /></label>' +
              '</div>' +
              '<div class="grid2">' +
                '<button type="button" data-agent-save="' + escapeHtml(agent.id) + '">Save</button>' +
                '<button type="button" class="secondary" data-agent-status="' + escapeHtml(agent.id) + '">' +
                  (agent.status === 'ACTIVE' ? 'Disable' : 'Enable') + '</button>' +
              '</div>' +
            '</div>';
          }).join('');
        }

        function renderFleetBundle() {
          const label = byId('fleetBundleVersion');
          if (!fleet.bundle) {
            label.textContent = 'none published';
            return;
          }
          label.textContent = 'version ' + fleet.bundle.version;
        }

        function renderFleetFallbacks() {
          const summaryTarget = byId('fleetFallbackSummary');
          const listTarget = byId('fleetFallbackList');
          const total = fleet.summary.reduce(function (sum, row) { return sum + row.total; }, 0);
          byId('fleetFallbackTotal').textContent = total + (total === 1 ? ' event' : ' events');

          if (!fleet.summary.length) {
            summaryTarget.innerHTML = '<div class="item muted">No fallback has been raised. That is the goal state.</div>';
            listTarget.innerHTML = '';
            return;
          }

          summaryTarget.innerHTML = fleet.summary.map(function (row) {
            // The agreement rate is the number that says whether a fallback is worth a rule:
            // if people always pick what the engine already proposed, the rule is nearly right
            // and the threshold is wrong. If they never do, the rule is wrong.
            const agreement = row.answered > 0 ? Math.round((row.agreedWithEngine / row.answered) * 100) : null;
            const median = row.medianDecisionMs == null ? null : Math.round(row.medianDecisionMs / 100) / 10;

            return '<div class="item">' +
              '<div class="section-title"><strong>' + escapeHtml(row.reasonCode) + '</strong>' +
              '<span class="pill">' + row.total + '</span></div>' +
              '<p class="muted">' + row.answered + ' answered' +
                (agreement == null ? '' : ' · ' + agreement + '% agreed with the engine') +
                (median == null ? '' : ' · median ' + median + 's to decide') + '</p>' +
            '</div>';
          }).join('');

          listTarget.innerHTML = fleet.fallbacks.slice(0, 20).map(function (event) {
            const engine = (event.engineSelection || []).join(', ') || 'none';
            const user = event.userSelection == null ? 'unanswered' : ((event.userSelection.join(', ')) || 'all A4');
            return '<div class="item">' +
              '<div class="section-title"><strong>' + escapeHtml(event.reasonCode) + '</strong>' +
              '<span class="pill">' + escapeHtml(fleetAgo(event.raisedAt)) + '</span></div>' +
              '<p class="muted">' + escapeHtml(event.message || '') + '</p>' +
              '<p class="hint muted">engine proposed ' + escapeHtml(engine) + ' · person chose ' + escapeHtml(user) + '</p>' +
            '</div>';
          }).join('') || '<div class="item muted">No individual events recorded.</div>';
        }

        /** Shows a derived rule with the reasoning behind it, ready to be read and edited. */
        function renderProposal(proposal) {
          const rationale = (proposal.rationale || []).map(function (line) {
            return '<li>' + escapeHtml(line) + '</li>';
          }).join('');

          return '<div class="item stack">' +
            '<strong>Proposed rule</strong>' +
            '<ul class="muted">' + rationale + '</ul>' +
            '<textarea rows="10" spellcheck="false" readonly>' +
              escapeHtml(JSON.stringify(proposal.rule, null, 2)) +
            '</textarea>' +
            '<button type="button" class="secondary" data-review-adopt="' +
              escapeHtml(JSON.stringify(proposal.rule)) + '">Add it to the bundle editor</button>' +
          '</div>';
        }

        /**
         * The resolution the sample is rendered at, and therefore the template's own dpi.
         *
         * pdf.js scale 1.0 is 72 dpi. Rendering at 150 and recording that with the template is
         * what lets the agent look for the logo at the physical size it was cut at, rather than
         * at whatever pixel size happened to fall out of the preview.
         */
        const FLEET_TEMPLATE_DPI = 150;

        const fleetSample = { pdf: null, page: 1, selection: null };

        async function fleetRenderSample() {
          if (!fleetSample.pdf) {
            return;
          }

          const canvas = byId('fleetSampleCanvas');
          const number = Math.min(Math.max(1, fleetSample.page), fleetSample.pdf.numPages);
          fleetSample.page = number;
          byId('fleetSamplePage').value = String(number);

          const page = await fleetSample.pdf.getPage(number);
          const viewport = page.getViewport({ scale: FLEET_TEMPLATE_DPI / 72 });
          canvas.width = Math.round(viewport.width);
          canvas.height = Math.round(viewport.height);
          await page.render({ canvasContext: canvas.getContext('2d'), viewport: viewport }).promise;

          fleetSample.selection = null;
          byId('fleetSampleSelection').style.display = 'none';
          byId('fleetSampleInfo').textContent = 'page ' + number + ' of ' + fleetSample.pdf.numPages;
        }

        /** Canvas pixels for a pointer event, which is not the same as CSS pixels. */
        function fleetCanvasPoint(event) {
          const canvas = byId('fleetSampleCanvas');
          const bounds = canvas.getBoundingClientRect();
          // The canvas is displayed scaled to fit, so a click at the right-hand edge is not
          // canvas.width CSS pixels from the left. Cropping on CSS coordinates would cut the
          // wrong region on every screen but the one it was written on.
          return {
            x: Math.round((event.clientX - bounds.left) * (canvas.width / bounds.width)),
            y: Math.round((event.clientY - bounds.top) * (canvas.height / bounds.height))
          };
        }

        function fleetDrawSelection() {
          const overlay = byId('fleetSampleSelection');
          const canvas = byId('fleetSampleCanvas');
          const box = fleetSample.selection;

          if (!box || box.w < 4 || box.h < 4) {
            overlay.style.display = 'none';
            return;
          }

          const bounds = canvas.getBoundingClientRect();
          const stage = byId('fleetSampleStage').getBoundingClientRect();
          const scale = bounds.width / canvas.width;

          overlay.style.display = 'block';
          overlay.style.left = (bounds.left - stage.left + (box.x * scale)) + 'px';
          overlay.style.top = (bounds.top - stage.top + (box.y * scale)) + 'px';
          overlay.style.width = (box.w * scale) + 'px';
          overlay.style.height = (box.h * scale) + 'px';
        }

        /** Cuts the selected region out as a PNG, base64, the way the bundle wants it. */
        function fleetCutTemplate() {
          const box = fleetSample.selection;
          if (!box || box.w < 8 || box.h < 8) {
            return null;
          }

          const cut = document.createElement('canvas');
          cut.width = box.w;
          cut.height = box.h;
          cut.getContext('2d').drawImage(
            byId('fleetSampleCanvas'), box.x, box.y, box.w, box.h, 0, 0, box.w, box.h);

          // The bundle carries bare base64; the validator rejects a data: URI prefix.
          const encoded = cut.toDataURL('image/png');
          return encoded.slice(encoded.indexOf(',') + 1);
        }

        function renderFleetJobs() {
          const target = byId('fleetJobList');
          byId('fleetJobCount').textContent = fleet.jobs.length + (fleet.jobs.length === 1 ? ' job' : ' jobs');

          if (!fleet.jobs.length) {
            target.innerHTML = '<div class="item muted">No job has been reported yet.</div>';
            byId('fleetJobDetail').innerHTML = '';
            return;
          }

          target.innerHTML = fleet.jobs.slice(0, 12).map(function (job) {
            const tone = job.status === 'COMPLETED' ? 'ok' : job.status === 'FAILED' ? 'danger' : 'warn';
            return '<div class="item">' +
              '<div class="section-title">' +
                '<strong>' + escapeHtml(job.fileName) + '</strong>' +
                '<span class="pill ' + tone + '">' + escapeHtml(job.status) + '</span>' +
              '</div>' +
              '<p class="muted">' + escapeHtml(fleetAgo(job.createdAt)) +
                ' · ' + job.pageCount + ' page(s)' +
                ' · ' + escapeHtml(job.source) +
                (job.userName ? ' · ' + escapeHtml(job.userName) : '') + '</p>' +
              '<button type="button" class="secondary" data-job="' + escapeHtml(job.id) + '">Show the pages</button>' +
            '</div>';
          }).join('');
        }

        function renderFleetJobDetail() {
          const target = byId('fleetJobDetail');
          const detail = fleet.jobDetail;

          if (!detail) {
            target.innerHTML = '';
            return;
          }

          if (!detail.pages.length) {
            target.innerHTML = '<div class="item muted">This job reported no per-page detail.</div>';
            return;
          }

          target.innerHTML = detail.pages.map(function (page) {
            const transform = page.transform || {};
            // The two fields that answer "why did it print at that size". Media comes through a
            // five-layer precedence chain, so the value alone would not be an explanation.
            const media = transform.effectiveMedia
              ? escapeHtml(transform.effectiveMedia) + ' (from ' + escapeHtml(transform.mediaSource || 'unknown') + ')'
              : 'not printed';

            return '<div class="item">' +
              '<div class="section-title">' +
                '<strong>Page ' + page.pageNumber + '</strong>' +
                '<span class="pill">' + escapeHtml(page.route || 'no route') + '</span>' +
              '</div>' +
              '<p class="muted">' + escapeHtml(page.printerQueue || 'no printer') + ' · ' + media + '</p>' +
              '<p class="hint muted">' +
                escapeHtml(page.ruleId || 'no rule matched') +
                (page.carrier ? ' · ' + escapeHtml(page.carrier) : '') +
                (page.confidence == null ? '' : ' · confidence ' + Number(page.confidence).toFixed(2)) +
                (transform.composeDpi ? ' · composed at ' + Math.round(transform.composeDpi) + ' dpi' : '') +
              '</p>' +
            '</div>';
          }).join('');
        }

        function renderFleetAccounting() {
          const target = byId('fleetAccountingList');
          const note = byId('fleetReconciliation');
          const data = fleet.accounting;

          if (!data || !data.rows.length) {
            byId('fleetAccountingTotal').textContent = '0 pages';
            note.textContent = 'Nothing printed in this window.';
            note.className = 'status muted';
            target.innerHTML = '';
            return;
          }

          const pages = data.rows.reduce(function (sum, row) { return sum + row.pages; }, 0);
          byId('fleetAccountingTotal').textContent = pages + (pages === 1 ? ' page' : ' pages');

          const r = data.reconciliation;
          const short = r.declaredPages - r.recordedPages;
          if (short === 0) {
            note.textContent = 'Reconciled: ' + r.completedJobs + ' completed job(s), ' +
              r.recordedPages + ' page(s), and every page accounted for.';
            note.className = 'status ok';
          } else {
            // Surfaced rather than smoothed over: a chargeback built on numbers nobody has
            // reconciled is a chargeback that will be disputed.
            note.textContent = 'Does not reconcile: agents declared ' + r.declaredPages +
              ' page(s) but reported ' + r.recordedPages + ' (' + Math.abs(short) +
              (short > 0 ? ' missing' : ' extra') + ') across ' + r.discrepancies.length + ' job(s).';
            note.className = 'status danger';
          }

          target.innerHTML = data.rows.slice(0, 30).map(function (row) {
            return '<div class="item">' +
              '<div class="section-title">' +
                '<strong>' + escapeHtml(row.machineName) + '</strong>' +
                '<span class="pill">' + row.pages + '</span>' +
              '</div>' +
              '<p class="muted">' + escapeHtml(row.userName || 'unknown user') +
                ' · ' + escapeHtml(row.route || 'not routed') +
                ' · ' + escapeHtml(row.printerQueue || 'no queue') +
                ' · ' + row.jobs + ' job(s)</p>' +
            '</div>';
          }).join('') + data.reconciliation.discrepancies.slice(0, 5).map(function (item) {
            return '<div class="item">' +
              '<div class="section-title"><strong>' + escapeHtml(item.fileName) + '</strong>' +
              '<span class="pill danger">' + item.recordedPages + ' of ' + item.declaredPages + '</span></div>' +
              '<p class="muted">' + escapeHtml(item.machineName) + ' reported fewer pages than the document had.</p>' +
            '</div>';
          }).join('');
        }

        function renderFleetReview() {
          const target = byId('fleetReviewList');
          const open = fleet.review.filter(function (item) { return item.status === 'OPEN'; });
          byId('fleetReviewCount').textContent = open.length + ' open';

          if (!open.length) {
            target.innerHTML = '<div class="item muted">Nothing waiting. Every answered fallback has been dealt with.</div>';
            return;
          }

          target.innerHTML = open.map(function (item) {
            return '<div class="item stack">' +
              '<div class="section-title"><strong>' + escapeHtml(item.reason) + '</strong>' +
              '<span class="pill">' + escapeHtml(fleetAgo(item.createdAt)) + '</span></div>' +
              '<label>What was done<input type="text" data-review-note="' + escapeHtml(item.id) + '" placeholder="Widened the inkAspect range on dhl-label-embedded" /></label>' +
              (item.proposedRule ? renderProposal(item.proposedRule) : '') +
              '<div class="grid2">' +
                '<button type="button" data-review-propose="' + escapeHtml(item.id) + '">Propose a rule</button>' +
                '<button type="button" data-review-resolve="' + escapeHtml(item.id) + '">Resolved</button>' +
              '</div>' +
              '<button type="button" class="secondary" data-review-dismiss="' + escapeHtml(item.id) + '">Dismiss</button>' +
            '</div>';
          }).join('');
        }

        async function loadFleet() {
          if (!state.token) {
            return;
          }

          try {
            const agents = await request('/admin/agents', 'GET');
            fleet.agents = agents.agents || [];
            renderFleetAgents();
          } catch (error) {
            fleetSetStatus('fleetTokenResult', 'Could not load agents: ' + error.message, 'danger');
          }

          try {
            // 404 is the ordinary "nothing published yet" answer, not a failure.
            const bundle = await request('/admin/bundles/latest', 'GET');
            fleet.bundle = bundle.bundle;
            byId('fleetBundleEditor').value = JSON.stringify(bundle.bundle.payload, null, 2);
          } catch (error) {
            fleet.bundle = null;
            if (!byId('fleetBundleEditor').value) {
              byId('fleetBundleEditor').value = JSON.stringify({ schemaVersion: 1, profiles: [] }, null, 2);
            }
          }
          renderFleetBundle();

          try {
            const summary = await request('/admin/fallbacks/summary', 'GET');
            fleet.summary = summary.summary || [];
            const events = await request('/admin/fallbacks?limit=20', 'GET');
            fleet.fallbacks = events.fallbacks || [];
            renderFleetFallbacks();
          } catch (error) {
            fleet.summary = [];
            fleet.fallbacks = [];
            renderFleetFallbacks();
          }

          try {
            const jobs = await request('/admin/agent-jobs?limit=12', 'GET');
            fleet.jobs = jobs.jobs || [];
            renderFleetJobs();
          } catch (error) {
            fleet.jobs = [];
            renderFleetJobs();
          }

          try {
            fleet.accounting = await request('/admin/accounting', 'GET');
            renderFleetAccounting();
          } catch (error) {
            fleet.accounting = null;
            renderFleetAccounting();
          }

          try {
            const review = await request('/admin/review-queue?status=OPEN', 'GET');
            fleet.review = review.items || [];
            renderFleetReview();
          } catch (error) {
            fleet.review = [];
            renderFleetReview();
          }
        }

        function bindFleet() {
          const tokenForm = byId('fleetTokenForm');
          if (tokenForm) {
            tokenForm.addEventListener('submit', async function (event) {
              event.preventDefault();
              try {
                const issued = await request('/admin/agents/enrollment-tokens', 'POST', {
                  label: byId('fleetTokenLabel').value.trim() || null,
                  validForHours: Number(byId('fleetTokenHours').value || 24),
                  maxUses: Number(byId('fleetTokenUses').value || 1)
                });
                fleetSetStatus('fleetTokenResult', 'Token (copy it now): ' + issued.token, 'ok');
              } catch (error) {
                fleetSetStatus('fleetTokenResult', 'Could not issue a token: ' + error.message, 'danger');
              }
            });
          }

          const publish = byId('fleetPublishButton');
          if (publish) {
            publish.addEventListener('click', async function () {
              let payload;
              try {
                payload = JSON.parse(byId('fleetBundleEditor').value);
              } catch (error) {
                fleetSetStatus('fleetBundleStatus', 'That is not valid JSON: ' + error.message, 'danger');
                return;
              }

              try {
                const published = await request('/admin/bundles', 'POST', {
                  payload: payload,
                  notes: byId('fleetBundleNotes').value.trim() || null
                });
                fleet.bundle = published.bundle;
                renderFleetBundle();
                fleetSetStatus('fleetBundleStatus', 'Published version ' + published.bundle.version + '. Agents pick it up within a minute.', 'ok');
              } catch (error) {
                // The server rejects a rule set neither engine could execute and names the
                // exact path, so the message is the useful part - show it whole.
                fleetSetStatus('fleetBundleStatus', 'Rejected: ' + error.message, 'danger');
              }
            });
          }

          const sampleFile = byId('fleetSampleFile');
          if (sampleFile) {
            sampleFile.addEventListener('change', async function (event) {
              const file = event.target.files && event.target.files[0];
              if (!file) {
                return;
              }

              try {
                const bytes = new Uint8Array(await file.arrayBuffer());
                fleetSample.pdf = await pdfjsLib.getDocument({ data: bytes }).promise;
                fleetSample.page = 1;
                await fleetRenderSample();
                fleetSetStatus('fleetTemplateStatus', 'Drag a box round the logo you want to match.', 'muted');
              } catch (error) {
                fleetSetStatus('fleetTemplateStatus', 'Could not open that PDF: ' + error.message, 'danger');
              }
            });
          }

          const samplePage = byId('fleetSamplePage');
          if (samplePage) {
            samplePage.addEventListener('change', function () {
              fleetSample.page = Number(samplePage.value || 1);
              void fleetRenderSample();
            });
          }

          const sampleCanvas = byId('fleetSampleCanvas');
          if (sampleCanvas) {
            let dragging = false;
            let origin = null;

            sampleCanvas.addEventListener('pointerdown', function (event) {
              if (!fleetSample.pdf) {
                return;
              }
              dragging = true;
              origin = fleetCanvasPoint(event);
              fleetSample.selection = { x: origin.x, y: origin.y, w: 0, h: 0 };
              sampleCanvas.setPointerCapture(event.pointerId);
            });

            sampleCanvas.addEventListener('pointermove', function (event) {
              if (!dragging || !origin) {
                return;
              }
              const point = fleetCanvasPoint(event);
              fleetSample.selection = {
                x: Math.min(origin.x, point.x),
                y: Math.min(origin.y, point.y),
                w: Math.abs(point.x - origin.x),
                h: Math.abs(point.y - origin.y)
              };
              fleetDrawSelection();
            });

            sampleCanvas.addEventListener('pointerup', function (event) {
              dragging = false;
              origin = null;
              fleetDrawSelection();

              if (fleetSample.selection && fleetSample.selection.w >= 8) {
                const mm = 25.4 / FLEET_TEMPLATE_DPI;
                fleetSetStatus(
                  'fleetTemplateStatus',
                  'Selected ' + Math.round(fleetSample.selection.w * mm) + ' x ' +
                    Math.round(fleetSample.selection.h * mm) + ' mm. Name it and add it.',
                  'ok');
              }

              try { sampleCanvas.releasePointerCapture(event.pointerId); } catch (_error) {}
            });
          }

          const addTemplate = byId('fleetAddTemplateButton');
          if (addTemplate) {
            addTemplate.addEventListener('click', function () {
              const name = byId('fleetTemplateName').value.trim();
              if (!name) {
                fleetSetStatus('fleetTemplateStatus', 'Give the template a name first.', 'danger');
                return;
              }

              const png = fleetCutTemplate();
              if (!png) {
                fleetSetStatus('fleetTemplateStatus', 'Drag a box round the logo first.', 'danger');
                return;
              }

              const editor = byId('fleetBundleEditor');
              let bundle;
              try {
                bundle = JSON.parse(editor.value);
              } catch (error) {
                fleetSetStatus('fleetTemplateStatus', 'Load a bundle first: ' + error.message, 'danger');
                return;
              }

              const profile = (bundle.profiles || [])[0];
              if (!profile) {
                fleetSetStatus('fleetTemplateStatus', 'The bundle has no profile to add the rule to.', 'danger');
                return;
              }

              // Replacing by name rather than appending: two templates with one name make every
              // rule using it ambiguous, and the validator refuses the bundle outright.
              bundle.templates = (bundle.templates || []).filter(function (entry) {
                return entry.name !== name;
              });
              bundle.templates.push({ name: name, png: png, dpi: FLEET_TEMPLATE_DPI });

              const ruleId = 'picture-' + name;
              profile.pageRules = [{
                id: ruleId,
                name: name + ' (picture match)',
                when: {
                  image: { template: name, threshold: Number(byId('fleetTemplateThreshold').value || 0.8) }
                },
                then: {
                  route: 'THERMAL',
                  confidence: 0.9,
                  transform: { source: 'inkBox', padMm: 1, rotate: 'auto', fit: 'contain' }
                }
              }].concat((profile.pageRules || []).filter(function (rule) { return rule.id !== ruleId; }));

              editor.value = JSON.stringify(bundle, null, 2);
              fleetSetStatus(
                'fleetTemplateStatus',
                'Added template ' + name + ' and rule ' + ruleId + ' to the editor. Review it, then publish.',
                'ok');
            });
          }

          const reload = byId('fleetLoadBundleButton');
          if (reload) {
            reload.addEventListener('click', function () { void loadFleet(); });
          }

          const agentList = byId('fleetAgentList');
          if (agentList) {
            agentList.addEventListener('click', async function (event) {
              const save = event.target.getAttribute && event.target.getAttribute('data-agent-save');
              const toggle = event.target.getAttribute && event.target.getAttribute('data-agent-status');

              if (save) {
                const mode = document.querySelector('[data-agent-mode="' + save + '"]');
                const threshold = document.querySelector('[data-agent-threshold="' + save + '"]');
                try {
                  await request('/admin/agents/' + encodeURIComponent(save), 'PATCH', {
                    decisionMode: mode ? mode.value : undefined,
                    confidenceThreshold: threshold ? Number(threshold.value) : undefined
                  });
                  await loadFleet();
                } catch (error) {
                  fleetSetStatus('fleetTokenResult', 'Could not update the agent: ' + error.message, 'danger');
                }
                return;
              }

              if (toggle) {
                const agent = fleet.agents.filter(function (entry) { return entry.id === toggle; })[0];
                try {
                  await request('/admin/agents/' + encodeURIComponent(toggle), 'PATCH', {
                    status: agent && agent.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE'
                  });
                  await loadFleet();
                } catch (error) {
                  fleetSetStatus('fleetTokenResult', 'Could not change the agent status: ' + error.message, 'danger');
                }
              }
            });
          }

          const jobList = byId('fleetJobList');
          if (jobList) {
            jobList.addEventListener('click', async function (event) {
              const id = event.target.getAttribute && event.target.getAttribute('data-job');
              if (!id) {
                return;
              }

              try {
                fleet.jobDetail = await request('/admin/agent-jobs/' + encodeURIComponent(id), 'GET');
                renderFleetJobDetail();
              } catch (error) {
                fleet.jobDetail = null;
                renderFleetJobDetail();
                fleetSetStatus('fleetTokenResult', 'Could not load the job: ' + error.message, 'danger');
              }
            });
          }

          const reviewList = byId('fleetReviewList');
          if (reviewList) {
            reviewList.addEventListener('click', async function (event) {
              const propose = event.target.getAttribute && event.target.getAttribute('data-review-propose');
              if (propose) {
                try {
                  await request('/admin/review-queue/' + encodeURIComponent(propose) + '/propose-rule', 'POST', {});
                  await loadFleet();
                } catch (error) {
                  fleetSetStatus('fleetTokenResult', 'Could not derive a rule: ' + error.message, 'danger');
                }
                return;
              }

              const adopt = event.target.getAttribute && event.target.getAttribute('data-review-adopt');
              if (adopt) {
                // Into the editor, never straight to the fleet. A machine-written rule is a
                // proposal an administrator reads, edits and publishes deliberately.
                const editor = byId('fleetBundleEditor');
                let bundle;
                try {
                  bundle = JSON.parse(editor.value);
                } catch (error) {
                  fleetSetStatus('fleetBundleStatus', 'Load a bundle first: ' + error.message, 'danger');
                  return;
                }

                const rule = JSON.parse(adopt);
                const profile = (bundle.profiles || [])[0];
                if (!profile) {
                  fleetSetStatus('fleetBundleStatus', 'The bundle has no profile to add the rule to.', 'danger');
                  return;
                }

                // Prepended: a more specific rule has to be tried before the general ones it
                // was derived to fix, or it never matches.
                profile.pageRules = [rule].concat(profile.pageRules || []);
                editor.value = JSON.stringify(bundle, null, 2);
                fleetSetStatus('fleetBundleStatus', 'Added to the editor. Review it, then publish.', 'ok');
                return;
              }

              const resolve = event.target.getAttribute && event.target.getAttribute('data-review-resolve');
              const dismiss = event.target.getAttribute && event.target.getAttribute('data-review-dismiss');
              const id = resolve || dismiss;
              if (!id) {
                return;
              }

              const note = document.querySelector('[data-review-note="' + id + '"]');
              try {
                await request('/admin/review-queue/' + encodeURIComponent(id) + '/resolve', 'POST', {
                  status: resolve ? 'RESOLVED' : 'DISMISSED',
                  resolution: note && note.value.trim() ? note.value.trim() : null
                });
                await loadFleet();
              } catch (error) {
                fleetSetStatus('fleetTokenResult', 'Could not close the review item: ' + error.message, 'danger');
              }
            });
          }
        }`;
}
