import { useCallback, useEffect, useRef, useState } from 'react';
import type { UserFile } from '@/queries/files.ts';

/**
 * Statuses that represent work still in progress (i.e., we should keep polling).
 */
const IN_PROGRESS_STATUSES = new Set(['Created', 'Queued', 'Processing']);

/**
 * How long a file should remain in the “recently completed” state (in ms).
 */
const RECENTLY_COMPLETED_HIGHLIGHT_MS = 60_000;

/**
 * Tracks file activity to drive polling behavior and brief “just completed” UI states.
 *
 * Usage pattern:
 * - Call `observeFiles(files)` whenever the latest file list changes (e.g., inside a query effect).
 * - Use `pollMs` as a polling interval (`false` means “stop polling”).
 * - Use `recentlyCompletedByFileId` to highlight items that transitioned to `Completed`
 *   within the last 60 seconds.
 */
export function useFileActivityPolling() {
  /**
   * Polling interval in ms, or `false` to disable polling.
   * Intended for wiring into query `refetchInterval`.
   */
  const [pollMs, setPollMs] = useState<number | false>(false);

  /**
   * Map of fileId -> completion timestamp (ms since epoch).
   * Entries expire automatically after 60 seconds.
   */
  const [recentlyCompletedByFileId, setRecentlyCompletedByFileId] = useState<
    Record<string, number>
  >({});

  // Internal timer handles for expiring `recentlyCompletedByFileId` entries.
  const recentlyCompletedTimersRef = useRef<Record<string, number>>({});

  // Used to avoid treating the initial list load as “status changes”.
  const hasSeenInitialFilesRef = useRef(false);

  // Previous statuses by fileId, used to detect transitions like Processing -> Completed.
  const prevStatusByFileIdRef = useRef<Record<string, string>>({});

  /**
   * Observe the current file list and update:
   * - `pollMs` (adaptive backoff while in-progress files exist)
   * - `recentlyCompletedByFileId` (when a file transitions to Completed)
   */
  const observeFiles = useCallback((files: UserFile[] | undefined) => {
    if (!files) {
      return;
    }

    const hasInProgressFiles = files.some((f) =>
      IN_PROGRESS_STATUSES.has(f.status)
    );

    const currentStatusById: Record<string, string> = {};
    for (const f of files) {
      currentStatusById[f.fileId] = f.status;
    }

    // First observation establishes the baseline; no “completion” signals yet.
    if (!hasSeenInitialFilesRef.current) {
      hasSeenInitialFilesRef.current = true;
      prevStatusByFileIdRef.current = currentStatusById;
      setPollMs(hasInProgressFiles ? 2000 : false);
      return;
    }

    let anyStatusChanged = false;
    const prev = prevStatusByFileIdRef.current;

    for (const f of files) {
      const prevStatus = prev[f.fileId];
      if (prevStatus && prevStatus !== f.status) {
        anyStatusChanged = true;

        // Only mark “recently completed” on an actual transition to Completed.
        if (prevStatus !== 'Completed' && f.status === 'Completed') {
          const fileId = f.fileId;

          setRecentlyCompletedByFileId((prevRecent) => ({
            ...prevRecent,
            [fileId]: Date.now(),
          }));

          const existingTimer = recentlyCompletedTimersRef.current[fileId];
          if (existingTimer) {
            window.clearTimeout(existingTimer);
          }

          recentlyCompletedTimersRef.current[fileId] = window.setTimeout(() => {
            setRecentlyCompletedByFileId((prevRecent) => {
              if (!(fileId in prevRecent)) {
                return prevRecent;
              }
              const next = { ...prevRecent };
              delete next[fileId];
              return next;
            });
            delete recentlyCompletedTimersRef.current[fileId];
          }, RECENTLY_COMPLETED_HIGHLIGHT_MS);
        }
      }
    }

    prevStatusByFileIdRef.current = currentStatusById;

    // If nothing is in progress, stop polling entirely.
    if (!hasInProgressFiles) {
      setPollMs(false);
      return;
    }

    // If work is actively changing, poll frequently.
    if (anyStatusChanged) {
      setPollMs(2000);
      return;
    }

    // Otherwise, gradually back off up to 8s while work is still in progress.
    setPollMs((prevMs) => {
      if (prevMs === false) {
        return 2000;
      }
      return Math.min(prevMs + 1000, 8000);
    });
  }, []);

  useEffect(() => {
    // Ensure we don’t leak timers if the component using this hook unmounts.
    return () => {
      for (const timer of Object.values(recentlyCompletedTimersRef.current)) {
        window.clearTimeout(timer);
      }
      recentlyCompletedTimersRef.current = {};
    };
  }, []);

  return { observeFiles, pollMs, recentlyCompletedByFileId };
}
