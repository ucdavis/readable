import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

type FailureTooltipState = {
  anchor: HTMLSpanElement;
  fileId: string;
  message: string;
};

type PdfFailureStatusBadgeProps = {
  failureReason: string;
  fileId: string;
  status: string;
};

export function PdfFailureStatusBadge({
  failureReason,
  fileId,
  status,
}: PdfFailureStatusBadgeProps) {
  const failureTooltipRef = useRef<HTMLDivElement>(null);
  const [failureTooltip, setFailureTooltip] =
    useState<FailureTooltipState | null>(null);
  const [failureTooltipPosition, setFailureTooltipPosition] = useState<{
    left: number;
    top: number;
  } | null>(null);

  const closeFailureTooltip = useCallback(() => {
    setFailureTooltip(null);
  }, []);

  const openFailureTooltip = useCallback(
    (message: string, anchor: HTMLSpanElement) => {
      setFailureTooltip({ anchor, fileId, message });
    },
    [fileId]
  );

  useEffect(() => {
    if (!failureTooltip) {
      setFailureTooltipPosition(null);
      return;
    }

    const updatePosition = () => {
      if (!failureTooltip.anchor.isConnected) {
        setFailureTooltip(null);
        return;
      }

      const rect = failureTooltip.anchor.getBoundingClientRect();
      const viewportPadding = 16;
      const fallbackTooltipWidth = Math.min(
        320,
        Math.max(0, window.innerWidth - viewportPadding * 2)
      );
      const tooltipWidth =
        failureTooltipRef.current?.getBoundingClientRect().width ??
        fallbackTooltipWidth;
      const tooltipHalfWidth = tooltipWidth / 2;
      const left = Math.min(
        Math.max(rect.left + rect.width / 2, viewportPadding + tooltipHalfWidth),
        window.innerWidth - viewportPadding - tooltipHalfWidth
      );

      setFailureTooltipPosition({
        left,
        top: rect.bottom + 12,
      });
    };

    updatePosition();

    window.addEventListener('resize', updatePosition);
    window.addEventListener('scroll', updatePosition, true);

    return () => {
      window.removeEventListener('resize', updatePosition);
      window.removeEventListener('scroll', updatePosition, true);
    };
  }, [failureTooltip]);

  return (
    <>
      <span
        aria-describedby={
          failureTooltip ? `failure-reason-${failureTooltip.fileId}` : undefined
        }
        className="badge badge-error badge-soft cursor-help"
        onBlur={closeFailureTooltip}
        onFocus={(e) => openFailureTooltip(failureReason, e.currentTarget)}
        onMouseEnter={(e) => openFailureTooltip(failureReason, e.currentTarget)}
        onMouseLeave={closeFailureTooltip}
        tabIndex={0}
      >
        {status}
      </span>

      {failureTooltip && typeof document !== 'undefined'
        ? createPortal(
            <div
              className="pointer-events-none fixed z-50 w-80 max-w-[calc(100vw-2rem)] -translate-x-1/2 rounded-xl border border-error/20 bg-base-100 p-4 text-left shadow-xl ring-1 ring-base-content/5"
              id={`failure-reason-${failureTooltip.fileId}`}
              ref={failureTooltipRef}
              role="tooltip"
              style={{
                left: failureTooltipPosition?.left ?? 0,
                top: failureTooltipPosition?.top ?? 0,
                visibility: failureTooltipPosition ? 'visible' : 'hidden',
              }}
            >
              <p className="text-xs font-semibold uppercase tracking-[0.2em] text-error">
                Failure reason
              </p>
              <p className="mt-2 text-sm leading-6 text-base-content/80">
                {failureTooltip.message}
              </p>
            </div>,
            document.body
          )
        : null}
    </>
  );
}
