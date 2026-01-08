import { useCallback, useEffect, useRef } from 'react';

export function useScheduledRefetch(
  refetch: () => Promise<unknown>,
  delayMs = 250
) {
  const timeoutRef = useRef<number | null>(null);

  useEffect(() => {
    return () => {
      if (timeoutRef.current !== null) {
        window.clearTimeout(timeoutRef.current);
        timeoutRef.current = null;
      }
    };
  }, []);

  return useCallback(() => {
    if (timeoutRef.current !== null) {
      return;
    }
    timeoutRef.current = window.setTimeout(() => {
      timeoutRef.current = null;
      void refetch();
    }, delayMs);
  }, [delayMs, refetch]);
}

