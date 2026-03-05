import { render } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useRouter } from '@tanstack/react-router';
import { AnalyticsListener } from '@/shared/analytics/AnalyticsListener.tsx';

vi.mock('@tanstack/react-router', () => ({
  useRouter: vi.fn(),
}));

describe('AnalyticsListener', () => {
  const useRouterMock = vi.mocked(useRouter);

  beforeEach(() => {
    window.gtag = vi.fn();
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

    expect(window.gtag).toHaveBeenCalledTimes(1);
    expect(window.gtag).toHaveBeenCalledWith('event', 'page_view', {
      page_path: window.location.pathname,
    });
    expect(subscribe).toHaveBeenCalledWith('onResolved', expect.any(Function));

    onResolvedHandler?.();
    expect(window.gtag).toHaveBeenCalledTimes(2);

    unmount();
    expect(unsubscribe).toHaveBeenCalledTimes(1);
  });
});
