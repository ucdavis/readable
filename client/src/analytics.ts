const GA_SCRIPT_ID = 'ga4-gtag-script';
const GA_MEASUREMENT_ID = 'G-3DS8QD8QV8';

let initialized = false;
let lastPageViewKey: string | null = null;

function isProductionBuild(): boolean {
  if (import.meta.env.MODE === 'test') {
    const isProdOverride = (
      globalThis as typeof globalThis & {
        __READABLE_ANALYTICS_IS_PROD__?: boolean;
      }
    ).__READABLE_ANALYTICS_IS_PROD__;

    if (typeof isProdOverride === 'boolean') {
      return isProdOverride;
    }
  }

  return Boolean(import.meta.env.PROD);
}

export function isAnalyticsEnabled(): boolean {
  return isProductionBuild();
}

export function initAnalytics(): void {
  if (!isAnalyticsEnabled()) {
    return;
  }

  if (initialized) {
    return;
  }

  if (!document.getElementById(GA_SCRIPT_ID)) {
    const script = document.createElement('script');
    script.id = GA_SCRIPT_ID;
    script.async = true;
    script.src = `https://www.googletagmanager.com/gtag/js?id=${encodeURIComponent(
      GA_MEASUREMENT_ID
    )}`;
    document.head.append(script);
  }

  window.dataLayer = window.dataLayer ?? [];
  window.gtag =
    window.gtag ??
    ((...args: unknown[]) => {
      window.dataLayer?.push(args);
    });

  window.gtag('js', new Date());
  window.gtag('config', GA_MEASUREMENT_ID, { send_page_view: false });
  initialized = true;
}

export function trackPageView(): void {
  if (!isAnalyticsEnabled() || !window.gtag) {
    return;
  }

  const pagePath = `${window.location.pathname}${window.location.search}`;
  const pageTitle = document.title;
  const pageLocation = window.location.href;
  const nextKey = `${pagePath}|${pageTitle}|${pageLocation}`;

  if (lastPageViewKey === nextKey) {
    return;
  }

  lastPageViewKey = nextKey;
  window.gtag('event', 'page_view', {
    page_location: pageLocation,
    page_path: pagePath,
    page_title: pageTitle,
  });
}
