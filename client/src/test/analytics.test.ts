import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

function setAnalyticsEnv(isProd: boolean) {
  (
    globalThis as typeof globalThis & {
      __READABLE_ANALYTICS_IS_PROD__?: boolean;
    }
  ).__READABLE_ANALYTICS_IS_PROD__ = isProd;
}

describe('analytics', () => {
  beforeEach(() => {
    vi.resetModules();
    document.head.innerHTML = '';
    document.title = '';
    window.dataLayer = undefined;
    window.gtag = undefined;
    window.history.pushState({}, '', '/');
  });

  afterEach(() => {
    (
      globalThis as typeof globalThis & {
        __READABLE_ANALYTICS_IS_PROD__?: boolean;
      }
    ).__READABLE_ANALYTICS_IS_PROD__ = undefined;
    window.dataLayer = undefined;
    window.gtag = undefined;
    document.head.innerHTML = '';
  });

  it('initAnalytics is a no-op when disabled', async () => {
    setAnalyticsEnv(false);

    const { initAnalytics } = await import('@/analytics.ts');

    initAnalytics();

    expect(document.querySelectorAll('script[src*="googletagmanager.com/gtag/js"]'))
      .toHaveLength(0);
  });

  it('initAnalytics loads script and configures gtag once in production', async () => {
    setAnalyticsEnv(true);

    const gtagSpy = vi.fn();
    window.gtag = gtagSpy;

    const { initAnalytics } = await import('@/analytics.ts');

    initAnalytics();
    initAnalytics();

    const scripts = document.querySelectorAll<HTMLScriptElement>(
      'script[src*="googletagmanager.com/gtag/js"]'
    );

    expect(scripts).toHaveLength(1);
    expect(scripts[0]?.src).toContain('id=G-3DS8QD8QV8');
    expect(gtagSpy).toHaveBeenCalledWith('js', expect.any(Date));
    expect(gtagSpy).toHaveBeenCalledWith('config', 'G-3DS8QD8QV8', {
      send_page_view: false,
    });
    expect(
      gtagSpy.mock.calls.filter((call) => call[0] === 'config')
    ).toHaveLength(1);
  });

  it('trackPageView emits page payload and suppresses duplicates', async () => {
    setAnalyticsEnv(true);

    const gtagSpy = vi.fn();
    window.gtag = gtagSpy;
    document.title = 'Reports';
    window.history.pushState({}, '', '/reports/123?view=full');
    const firstLocation = window.location.href;

    const { trackPageView } = await import('@/analytics.ts');

    trackPageView();
    trackPageView();
    window.history.pushState({}, '', '/reports/124?view=full');
    trackPageView();

    expect(gtagSpy).toHaveBeenNthCalledWith(1, 'event', 'page_view', {
      page_location: firstLocation,
      page_path: '/reports/123?view=full',
      page_title: 'Reports',
    });
    expect(gtagSpy).toHaveBeenNthCalledWith(2, 'event', 'page_view', {
      page_location: window.location.href,
      page_path: '/reports/124?view=full',
      page_title: 'Reports',
    });
    expect(gtagSpy).toHaveBeenCalledTimes(2);
  });
});
