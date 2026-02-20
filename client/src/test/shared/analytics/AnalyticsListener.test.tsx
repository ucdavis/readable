import { render } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useRouter } from '@tanstack/react-router';
import { trackPageView } from '@/analytics.ts';
import { AnalyticsListener } from '@/shared/analytics/AnalyticsListener.tsx';

vi.mock('@tanstack/react-router', () => ({
  useRouter: vi.fn(),
}));

vi.mock('@/analytics.ts', () => ({
  trackPageView: vi.fn(),
}));

describe('AnalyticsListener', () => {
  const useRouterMock = vi.mocked(useRouter);
  const trackPageViewMock = vi.mocked(trackPageView);

  beforeEach(() => {
    trackPageViewMock.mockReset();
  });

  it('tracks on mount and on resolved navigation', () => {
    const unsubscribe = vi.fn();
    let onResolvedHandler: (() => void) | undefined;
    const subscribe = vi.fn((eventType: string, handler: () => void) => {
      if (eventType === 'onResolved') {
        onResolvedHandler = handler;
      }
      return unsubscribe;
    });

    useRouterMock.mockReturnValue({ subscribe } as never);

    const { unmount } = render(<AnalyticsListener />);

    expect(trackPageViewMock).toHaveBeenCalledTimes(1);
    expect(subscribe).toHaveBeenCalledWith('onResolved', expect.any(Function));

    onResolvedHandler?.();
    expect(trackPageViewMock).toHaveBeenCalledTimes(2);

    unmount();
    expect(unsubscribe).toHaveBeenCalledTimes(1);
  });
});
