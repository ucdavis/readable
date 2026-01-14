import { formatDateTime } from '@/lib/format.ts';
import {
  myFileQueryOptions,
  type AccessibilityReportDetails,
  type AccessibilityReportJson,
} from '@/queries/files.ts';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link } from '@tanstack/react-router';
import { useMemo } from 'react';

export const Route = createFileRoute('/(authenticated)/reports/$fileId/')({
  component: RouteComponent,
});

type SummaryCounts = {
  description?: string;
  failed: number;
  needsManual: number;
  passed: number;
  skipped: number;
  total: number;
};

type FlattenedRule = {
  category: string;
  description?: string;
  rule: string;
  status: string;
};

function getSummaryCounts(reportJson: AccessibilityReportJson | undefined) {
  const summary = reportJson?.Summary;
  if (!summary || typeof summary !== 'object') {
    return null;
  }

  const num = (key: string) =>
    typeof summary[key] === 'number' ? summary[key] : 0;

  const passed = num('Passed') + num('Passed manually');
  const failed = num('Failed') + num('Failed manually');
  const needsManual = num('Needs manual check');
  const skipped = num('Skipped');
  const total = Object.values(summary).reduce<number>(
    (acc, v) => acc + (typeof v === 'number' ? v : 0),
    0
  );

  return {
    description:
      typeof summary.Description === 'string' ? summary.Description : undefined,
    failed,
    needsManual,
    passed,
    skipped,
    total,
  } satisfies SummaryCounts;
}

function flattenDetailedReport(report: AccessibilityReportDetails | undefined) {
  const detailed = report?.reportJson?.['Detailed Report'];
  if (!detailed || typeof detailed !== 'object') {
    return [];
  }

  const rows: FlattenedRule[] = [];
  for (const [category, items] of Object.entries(detailed)) {
    if (!Array.isArray(items)) {
      continue;
    }
    for (const item of items) {
      if (!item || typeof item !== 'object') {
        continue;
      }
      const record = item as Record<string, unknown>;

      const rule = record.Rule;
      const status = record.Status;
      if (typeof rule !== 'string' || typeof status !== 'string') {
        continue;
      }

      rows.push({
        category,
        description:
          typeof record.Description === 'string'
            ? record.Description
            : undefined,
        rule,
        status,
      });
    }
  }

  return rows;
}

function isFailedStatus(status: string) {
  return status.toLowerCase().includes('failed');
}

function isNeedsManualStatus(status: string) {
  return status.toLowerCase().includes('needs manual');
}

function statusBadgeClass(status: string) {
  const s = status.toLowerCase();
  if (s.includes('failed')) {
    return 'badge-error';
  }
  if (s.includes('passed')) {
    return 'badge-success';
  }
  if (s.includes('needs manual')) {
    return 'badge-warning';
  }
  if (s.includes('skipped')) {
    return 'badge-ghost';
  }
  return 'badge-ghost';
}

function RouteComponent() {
  const { fileId } = Route.useParams();
  const fileQuery = useQuery(myFileQueryOptions(fileId));

  const {
    afterCounts,
    afterReport,
    afterRows,
    beforeCounts,
    beforeReport,
    compareByCategory,
    extrasByStage,
  } = useMemo(() => {
    const reports = fileQuery.data?.accessibilityReports ?? [];

    const stageKey = (stage: string) => stage.trim().toLowerCase();
    const stageBuckets = new Map<string, AccessibilityReportDetails[]>();
    for (const r of reports) {
      const key = stageKey(r.stage);
      const bucket = stageBuckets.get(key) ?? [];
      bucket.push(r);
      stageBuckets.set(key, bucket);
    }

    const pickLatest = (rs: AccessibilityReportDetails[]) => {
      return [...rs].sort((a, b) => {
        return (
          new Date(b.generatedAt).getTime() - new Date(a.generatedAt).getTime()
        );
      })[0];
    };

    const beforeBucket = stageBuckets.get('before') ?? [];
    const afterBucket = stageBuckets.get('after') ?? [];
    const before = beforeBucket.length ? pickLatest(beforeBucket) : undefined;
    const after = afterBucket.length ? pickLatest(afterBucket) : undefined;

    const beforeFlat = flattenDetailedReport(before);
    const afterFlat = flattenDetailedReport(after);

    const beforeCounts = getSummaryCounts(before?.reportJson);
    const afterCounts = getSummaryCounts(after?.reportJson);

    const beforeByKey = new Map<string, FlattenedRule>();
    for (const row of beforeFlat) {
      beforeByKey.set(`${row.category}||${row.rule}`, row);
    }
    const afterByKey = new Map<string, FlattenedRule>();
    for (const row of afterFlat) {
      afterByKey.set(`${row.category}||${row.rule}`, row);
    }

    const rulesByCategory = new Map<string, string[]>();
    const ensureRule = (category: string, rule: string) => {
      const list = rulesByCategory.get(category) ?? [];
      if (!list.includes(rule)) {
        list.push(rule);
      }
      rulesByCategory.set(category, list);
    };

    for (const row of afterFlat) {
      ensureRule(row.category, row.rule);
    }
    for (const row of beforeFlat) {
      ensureRule(row.category, row.rule);
    }

    const categories = [...rulesByCategory.keys()].sort((a, b) =>
      a.localeCompare(b)
    );

    const compareByCategory = categories.map((category) => {
      const rules = rulesByCategory.get(category) ?? [];
      const rows = rules.map((rule) => {
        const key = `${category}||${rule}`;
        const before = beforeByKey.get(key);
        const after = afterByKey.get(key);
        return {
          afterStatus: after?.status,
          beforeStatus: before?.status,
          category,
          description: after?.description ?? before?.description,
          rule,
        };
      });

      const afterFailedCount = rows.filter((r) =>
        r.afterStatus ? isFailedStatus(r.afterStatus) : false
      ).length;
      const afterNeedsManualCount = rows.filter((r) =>
        r.afterStatus ? isNeedsManualStatus(r.afterStatus) : false
      ).length;

      return {
        afterFailedCount,
        afterNeedsManualCount,
        category,
        rows,
      };
    });

    const extrasByStage = {
      after: afterBucket.length > 1 ? afterBucket.length - 1 : 0,
      before: beforeBucket.length > 1 ? beforeBucket.length - 1 : 0,
    };

    return {
      afterCounts,
      afterReport: after,
      afterRows: afterFlat,
      beforeCounts,
      beforeReport: before,
      compareByCategory,
      extrasByStage,
    };
  }, [fileQuery.data?.accessibilityReports]);

  if (fileQuery.isError) {
    return (
      <div className="container my-6">
        <div className="alert alert-error">
          <span>Failed to load this report.</span>
        </div>
      </div>
    );
  }

  if (!fileQuery.data) {
    return (
      <div className="container my-6">
        <div className="flex items-center gap-3 text-base-content/70">
          <span className="loading loading-spinner loading-sm" />
          <span>Loading report…</span>
        </div>
      </div>
    );
  }

  const file = fileQuery.data;
  const isCompleted = file.status === 'Completed';

  const afterFailed = afterRows.filter((r) => isFailedStatus(r.status));
  const afterNeedsManual = afterRows.filter((r) =>
    isNeedsManualStatus(r.status)
  );

  return (
    <div className="container">
      <header className="space-y-3 my-5">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="space-y-1">
            <h1 className="text-2xl sm:text-3xl font-extrabold tracking-tight">
              Accessibility report
            </h1>
            <div className="text-base-content/70">
              <div className="flex flex-wrap items-center gap-2">
                <span className="badge badge-ghost">{file.status}</span>
                <span className="text-sm">{file.originalFileName}</span>
              </div>
              <div className="text-xs mt-1">
                Updated {formatDateTime(file.statusUpdatedAt)}
              </div>
            </div>
          </div>

          <Link className="btn btn-ghost btn-sm" to="/">
            Back
          </Link>
        </div>

        {!isCompleted ? (
          <div className="alert alert-warning">
            <span>
              This file is not completed yet. Reports may be missing or
              incomplete.
            </span>
          </div>
        ) : null}

        {!file.accessibilityReports ||
        file.accessibilityReports.length === 0 ? (
          <div className="alert alert-info">
            <span>No accessibility reports found for this file yet.</span>
          </div>
        ) : null}

        {extrasByStage.before > 0 || extrasByStage.after > 0 ? (
          <div className="alert alert-info">
            <span>
              Multiple reports found. Showing the most recent{' '}
              {beforeReport ? '"Before"' : ''}{' '}
              {beforeReport && afterReport ? 'and' : ''}{' '}
              {afterReport ? '"After"' : ''} report.
            </span>
          </div>
        ) : null}

        {file.accessibilityReports?.length ? (
          !beforeReport || !afterReport ? (
            <div className="alert alert-warning">
              <span>
                Expected both a “Before” and “After” report, but found:{' '}
                {file.accessibilityReports.map((r) => r.stage).join(', ')}.
              </span>
            </div>
          ) : null
        ) : null}
      </header>

      {beforeReport && afterReport && beforeCounts && afterCounts ? (
        <section className="space-y-4">
          <div className="stats stats-vertical lg:stats-horizontal shadow bg-base-100 w-full">
            <div className="stat">
              <div className="stat-title">Before</div>
              <div className="stat-value text-xl">
                {beforeCounts.passed}/{beforeCounts.total}
              </div>
              <div className="stat-desc">
                {beforeCounts.failed} failed • {beforeCounts.needsManual} needs
                manual
              </div>
              <div className="stat-desc">
                Generated {formatDateTime(beforeReport.generatedAt)}
              </div>
            </div>

            <div className="stat">
              <div className="stat-title">After</div>
              <div className="stat-value text-xl">
                {afterCounts.passed}/{afterCounts.total}
              </div>
              <div className="stat-desc">
                {afterCounts.failed} failed • {afterCounts.needsManual} needs
                manual
              </div>
              <div className="stat-desc">
                Generated {formatDateTime(afterReport.generatedAt)}
              </div>
            </div>

            <div className="stat">
              <div className="stat-title">Improvement</div>
              <div className="stat-value text-xl">
                {afterCounts.passed - beforeCounts.passed >= 0 ? '+' : ''}
                {afterCounts.passed - beforeCounts.passed}
              </div>
              <div className="stat-desc">passed checks</div>
            </div>
          </div>

          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <div className="card bg-base-100 shadow">
              <div className="card-body">
                <h2 className="card-title">Still failing (After)</h2>
                {afterFailed.length === 0 ? (
                  <div className="alert alert-success">
                    <span>No failed checks found in the After report.</span>
                  </div>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="table table-sm">
                      <thead>
                        <tr>
                          <th>Category</th>
                          <th>Rule</th>
                          <th>Status</th>
                        </tr>
                      </thead>
                      <tbody>
                        {afterFailed.map((r, idx) => (
                          <tr key={`${r.category}||${r.rule}||${idx}`}>
                            <td className="text-base-content/80">
                              {r.category}
                            </td>
                            <td>
                              <div className="font-medium">{r.rule}</div>
                              {r.description ? (
                                <div className="text-xs text-base-content/60">
                                  {r.description}
                                </div>
                              ) : null}
                            </td>
                            <td>
                              <span
                                className={`badge badge-sm ${statusBadgeClass(
                                  r.status
                                )}`}
                              >
                                {r.status}
                              </span>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            </div>

            <div className="card bg-base-100 shadow">
              <div className="card-body">
                <h2 className="card-title">Needs manual check (After)</h2>
                {afterNeedsManual.length === 0 ? (
                  <div className="alert alert-success">
                    <span>
                      No “Needs manual check” items found in the After report.
                    </span>
                  </div>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="table table-sm">
                      <thead>
                        <tr>
                          <th>Category</th>
                          <th>Rule</th>
                          <th>Status</th>
                        </tr>
                      </thead>
                      <tbody>
                        {afterNeedsManual.map((r, idx) => (
                          <tr key={`${r.category}||${r.rule}||${idx}`}>
                            <td className="text-base-content/80">
                              {r.category}
                            </td>
                            <td>
                              <div className="font-medium">{r.rule}</div>
                              {r.description ? (
                                <div className="text-xs text-base-content/60">
                                  {r.description}
                                </div>
                              ) : null}
                            </td>
                            <td>
                              <span
                                className={`badge badge-sm ${statusBadgeClass(
                                  r.status
                                )}`}
                              >
                                {r.status}
                              </span>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            </div>
          </div>

          <div className="card bg-base-100 shadow">
            <div className="card-body">
              <h2 className="card-title">Before vs After breakdown</h2>
              <div className="space-y-3">
                {compareByCategory.map((c) => (
                  <div
                    className="collapse collapse-arrow bg-base-200"
                    key={c.category}
                  >
                    <input defaultChecked={false} type="checkbox" />
                    <div className="collapse-title font-medium flex flex-wrap items-center gap-2">
                      <span>{c.category}</span>
                      {c.afterFailedCount > 0 ? (
                        <span className="badge badge-error badge-sm">
                          {c.afterFailedCount} failed
                        </span>
                      ) : (
                        <span className="badge badge-success badge-sm">
                          all passed
                        </span>
                      )}
                      {c.afterNeedsManualCount > 0 ? (
                        <span className="badge badge-warning badge-sm">
                          {c.afterNeedsManualCount} manual
                        </span>
                      ) : null}
                    </div>
                    <div className="collapse-content">
                      <div className="overflow-x-auto">
                        <table className="table table-sm">
                          <thead>
                            <tr>
                              <th>Rule</th>
                              <th>Before</th>
                              <th>After</th>
                            </tr>
                          </thead>
                          <tbody>
                            {c.rows.map((r) => (
                              <tr key={`${r.category}||${r.rule}`}>
                                <td>
                                  <div className="font-medium">{r.rule}</div>
                                  {r.description ? (
                                    <div className="text-xs text-base-content/60">
                                      {r.description}
                                    </div>
                                  ) : null}
                                </td>
                                <td>
                                  {r.beforeStatus ? (
                                    <span
                                      className={`badge badge-sm ${statusBadgeClass(
                                        r.beforeStatus
                                      )}`}
                                    >
                                      {r.beforeStatus}
                                    </span>
                                  ) : (
                                    <span className="text-base-content/50">
                                      —
                                    </span>
                                  )}
                                </td>
                                <td>
                                  {r.afterStatus ? (
                                    <span
                                      className={`badge badge-sm ${statusBadgeClass(
                                        r.afterStatus
                                      )}`}
                                    >
                                      {r.afterStatus}
                                    </span>
                                  ) : (
                                    <span className="text-base-content/50">
                                      —
                                    </span>
                                  )}
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </section>
      ) : null}
    </div>
  );
}
